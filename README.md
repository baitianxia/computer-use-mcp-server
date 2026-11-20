# Windows Computer Use for Claude Code

本地功能验证版：让 **Claude Code CLI** 通过标准 MCP stdio 工具发现 Windows 窗口、读取 UI Automation、截图并执行桌面动作。推理和任务规划由 Claude Code 负责；MCP 不调用模型。

**当前验证范围：** 已在 GitHub Actions 的 **Windows Server 2025 x64** 上通过构建、MCP 协议和 Demo 原生桌面测试，覆盖 UIA 查询、WGC 截图、截图坐标点击、Unicode 键盘输入、弹窗及窗口恢复。Windows 10/11 兼容性、Claude Code 实际会话和真实业务客户端仍未验证。详见 [验收记录](docs/acceptance.md) 和 [Windows CI](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/43862520352)。

本轮增加了控件筛选与状态、条件等待、更多 UIA 动作、WPF 原生矩阵和自包含集中测试包。当前改动的 Windows 运行结果以验收记录为准。

**带到 Windows 测试：** 获取 `windows-cu-win-x64.zip`，完整解压，双击 `Start-Test.cmd`；然后运行 `Setup-Claude.cmd` 接入 Claude Code。[集中测试步骤](docs/windows-testing.md)。

## 交付结构

| 路径 | 用途 |
|---|---|
| `src/WindowsComputerUse.Server` | 官方 MCP C# SDK 接入，stdio、图片结果、超时、中止、基础日志 |
| `src/WindowsComputerUse.Worker` | Windows 用户会话中的 Win32 / UIA3 / 键鼠 / 截图执行进程 |
| `src/WindowsComputerUse.Core` | 数据结构、动作参数检查、短期引用、坐标映射 |
| `.claude/skills/windows-desktop` | Claude Code 通用桌面操作 Skill |
| `.claude/skills/cu-demo-orders` | Claude Code 示例客户端 Skill |
| `templates/claude-client-skill` | 未安装的 Claude Code 客户端 Skill 模板 |
| `samples/CuDemo` | 带查询、旧结果、弹窗、自绘图形的 WinForms 测试客户端 |
| `samples/CuWpfDemo` | 多选、树节点、滑块、滚动和原生鼠标事件的 WPF 测试客户端 |
| `Start-Test.cmd` / `Setup-Claude.cmd` | 集中测试入口 / Claude Code 项目 MCP 配置入口 |
| `tests/WindowsComputerUse.Tests` | 可移植测试、真实 MCP 协议探针、Windows 示例闭环探针 |

这些 Skill 使用 Claude Code 的 `.claude/skills/<名称>/SKILL.md` 格式；MCP 注册使用 `claude mcp add` / `.mcp.json`。没有 Codex 专用配置。

## 在 Windows 上使用

当前版本以 Windows 10 2004（build 19041）及以上或 Windows 11、当前登录且未锁定的用户桌面为目标。首个发布包为 `win-x64`。建议在 Windows 原生 PowerShell/Terminal 运行 Claude Code；WSL 桥接未验证。

### 1. 构建或使用已发布文件

从源码构建需要 .NET 10 SDK。在项目目录执行：

```powershell
.\scripts\Build.ps1
```

脚本会构建、运行通用测试，并生成自包含程序到 `artifacts/win-x64/`。生成 `artifacts/windows-cu-win-x64.zip`，其中 `server`、`worker`、`demo`、`wpf`、`tester` 均包含所需运行时。运行整个测试包无需另外安装 .NET 或 SDK。请保留完整目录关系；不要只复制一个 exe。

可直接启动测试客户端：

```powershell
.\artifacts\win-x64\demo\CuDemo.exe
```

### 2. 注册到 Claude Code

先确认 `claude --version` 可用，然后执行：

```powershell
.\scripts\Register-ClaudeMcp.ps1 -Scope project
claude
```

默认 project scope 在当前目录创建/更新标准 `.mcp.json`，按实际位置写入 exe 路径，并保留其他 MCP 配置、备份旧文件。`local/user` scope 通过 `claude mcp add --transport stdio --scope ...` 注册。目录移动后重新运行配置脚本。

项目 `.claude/skills` 随项目被发现；需要在所有项目使用 Skill 时，将 Skill 目录复制到 `%USERPROFILE%\.claude\skills\`。也可参考 `.mcp.json.example` 手动配置。项目配置写入或 CLI 查询成功不等于模型业务验收。

重新进入 Claude Code 后使用 `/mcp` 检查 `windows_cu` 是否连接，并确认存在：

```text
list_windows  inspect_window  capture_window  perform_action  wait_for  stop
```

Claude Code 自身可能要求信任项目级 MCP 配置或批准工具调用，按 CLI 提示处理。项目未增加完整的业务审批系统，也不修改 Claude Code 的权限模式。

### 3. 在 Claude Code 中验证 Skill 与闭环

在项目目录启动的新会话里输入：

```text
/cu-demo-orders 查询 A1001，核对订单号、状态、金额和本次查询序号。
```

然后测试自动匹配：

```text
在 CU Demo Orders 中查询 A1002，确认是本次查询结果，然后告诉我订单状态。
```

视觉回退与弹窗示例：

```text
在 CU Demo Orders 中，通过截图找到画布里的 Orange 区域，点击后核对选择结果。允许前台操作。
在 CU Demo Orders 中打开 Show dialog，识别弹窗并关闭它，再重新观察主窗口。
```

后台示例：

```text
仅在后台查询 CU Demo Orders 的 A1001；无法后台完成就停止，说明原因。
```

## 验证

通用测试（不验证 Windows API）：

```powershell
dotnet run --project tests/WindowsComputerUse.Tests -c Release
```

Windows 集中测试使用发布包内的独立测试器，需要交互桌面；无需 SDK。源码目录先构建：

```powershell
.\scripts\Test-Windows.ps1
```

解压包可双击 `Start-Test.cmd`。脚本依次进行文件校验、通用测试、MCP 协议、WinForms 闭环、WPF/UIA/原生鼠标/遮挡与后台焦点采样。结果写入 `test-results/<时间>/`，同时生成可反馈的 ZIP；失败、阻塞和未执行分别记录，重复运行保留旧报告。[测试覆盖与人工步骤](docs/windows-testing.md)。

CI 会在含空格和中文的路径解压发布包，用 Windows PowerShell 5.1 运行同一入口，并使用实际 Claude Code CLI 检查配置及 MCP 连接。CLI 健康检查不调用模型。报告保留 14 天，通过后的发布包保留 30 天；已提交的关键证据见 [验收记录](docs/acceptance.md)。

WGC 失败使对应测试失败，不用其他截图后端掩盖。已知颜色检测验证截图/坐标映射，不代表 Claude Code 的视觉理解或自主规划；Windows CI 也不替代真实 Windows 10/11 和业务客户端验收。

## 已实现的接口与边界

见 [接口说明](docs/interfaces.md)。核心行为：

- 窗口引用绑定 HWND、PID 和进程启动时间，窗口重开后重新枚举。
- 控件与截图引用有效期两分钟；同一窗口执行动作后旧观察失效。
- 窗口与控件可按多个特征联合筛选；返回切换、选中、展开、只读、数值和滚动状态。条件等待支持文本、存在/消失与控件状态，裁剪观察不能证明控件消失。
- UIA 支持 invoke、set_value、toggle、select/add_selection/remove_selection、expand/collapse、set_range_value、scroll_pattern、scroll_into_view。
- 优先使用 UIA pattern，键鼠输入显式属于前台动作。`background_only` 不降级成前台输入。
- 截图默认 `auto`：先尝试 **WGC（Windows Graphics Capture）**，在不支持、设备创建失败、无新帧超时或一般捕获错误时回退到 `PrintWindow`。返回实际后端及回退原因；不自动激活窗口或转为屏幕截图。
- `method: wgc` 强制使用 WGC，失败直接返回错误。`print_window` 和 `screen` 可显式选择；后者要求目标已处于前台。
- WGC 按请求新建捕获会话，等待本次会话返回图像帧，输出 SDR 8-bit PNG。最小化窗口拒绝截图；移动/缩放期间拒绝不确定坐标，需重新截图。截图时间不等于业务数据刷新；HDR 色彩、受保护内容和具体应用兼容性需实测。
- UIA `AutoSetFocus` 已禁用；客户端自行聚焦/弹窗仍可能影响前台。具体后台能力需逐客户端验证。
- 键鼠动作前核对前台窗口和点击点归属；这不构成对用户同时输入的完全隔离。
- 拖拽以一批短输入事件执行，暂不提供可配置的按住时长，复杂拖放应用需验证。
- `set_value` 设置控件值；`type_text` 通过 Unicode 输入追加文字。不同输入法、应用和复杂文本控件的行为待实测。
- 动作返回 `execution=executed` 时，业务验证仍为 `not_verified`。由 Claude Code 根据新观察和 Skill 验证任务。
- 发生超时/中止时终止执行进程并使引用失效。已送达的动作不回滚，未知结果不自动重试。
- 当前不包含应用白名单、细粒度授权、业务高风险审批和完整安全审计；这些按约定后置。

## 日志与排障

基础 JSONL 日志默认位于 `%LOCALAPPDATA%\WindowsComputerUse\logs\`，记录操作名称、窗口引用、耗时和错误，不记录全部输入文本或图片。MCP 协议独占 stdout，诊断输出写入 stderr。

可配置环境变量：

| 环境变量 | 作用 |
|---|---|
| `WINDOWS_CU_WORKER_PATH` | 指定 Windows worker exe 或 dll；默认使用相邻的 `worker` 目录 |
| `WINDOWS_CU_LOG_DIR` | 指定基础日志目录 |

常见问题：

- `unsupported_platform`：Windows 操作在非 Windows 系统调用。macOS 只能构建和执行协议/通用测试。
- `worker_unavailable`：检查发布目录是否完整，或设置 `WINDOWS_CU_WORKER_PATH`。从源码单独运行 Server 时也需要指定 Worker。
- `stale_*`：重新枚举窗口或观察界面，使用新引用。
- `foreground_required`：当前动作需要前台；后台任务应停止或改用支持的 UIA pattern。
- `foreground_not_target`：Windows 未允许激活窗口，或焦点已改变；没有继续发送这一步输入。
- `pattern_unsupported`：控件不提供该操作模式。检查其他模式或明确选择视觉回退。
- `cancelled_or_timeout`：执行进程停止；先读取实际状态，不能推断之前的提交没有发生。
- `wgc_*`：查看 WGC 错误原因；`auto` 可回退的错误会记录在 `fallbackError`，`method` 表示实际使用的后端。强制 WGC 不回退。
- `capture_geometry_mismatch`：帧尺寸无法匹配已知窗口边界，未猜测坐标；重新观察并截图。仍失败时保留错误、分辨率和 DPI 信息用于排查。
- 黑图/缺失图：先核对实际截图后端和图片内容。允许前台任务时可显式使用屏幕截图；不要用黑图猜坐标。系统禁止捕获的内容不会通过自动回退处理。
- 管理员程序、锁屏/UAC、安全桌面：当前不自动提权，也不绕过系统限制。

## 规范来源

- [Claude Code MCP](https://code.claude.com/docs/en/mcp)
- [Claude Code Skills](https://code.claude.com/docs/en/skills)
- [官方 MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [FlaUI UIA3](https://github.com/FlaUI/FlaUI)
- [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)
- [WGC Win32 窗口接入](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)

## 下一步

继续在 Windows 10/11 上验证兼容性和 Claude Code 实际会话，再选择首个真实客户端。当前 CI 已验证的范围见验收记录；虚拟化表格、事件驱动等待、更多客户端和完整安全治理尚未完成。
