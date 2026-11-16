# Windows Computer Use for Claude Code

本地功能验证版：让 **Claude Code CLI** 通过标准 MCP stdio 工具发现 Windows 窗口、读取 UI Automation、截图并执行桌面动作。推理和任务规划由 Claude Code 负责；MCP 不调用模型。

**当前验证范围：** 已在 GitHub Actions 的 **Windows Server 2025 x64** 上通过构建、MCP 协议和 Demo 原生桌面测试，覆盖 UIA 查询、WGC 截图、截图坐标点击、Unicode 键盘输入、弹窗及窗口恢复。Windows 10/11 兼容性、Claude Code 实际会话和真实业务客户端仍未验证。详见 [验收记录](docs/acceptance.md) 和 [Windows CI](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/43862520352)。

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
| `tests/WindowsComputerUse.Tests` | 可移植测试、真实 MCP 协议探针、Windows 示例闭环探针 |

这些 Skill 使用 Claude Code 的 `.claude/skills/<名称>/SKILL.md` 格式；MCP 注册使用 `claude mcp add` / `.mcp.json`。没有 Codex 专用配置。

## 在 Windows 上使用

当前版本以 Windows 10 2004（build 19041）及以上或 Windows 11、当前登录且未锁定的用户桌面为目标。首个发布包为 `win-x64`。建议在 Windows 原生 PowerShell/Terminal 运行 Claude Code；WSL 桥接未验证。

### 1. 构建或使用已发布文件

从源码构建需要 .NET 10 SDK。在项目目录执行：

```powershell
.\scripts\Build.ps1
```

脚本会构建、运行通用测试，并生成自包含程序到 `artifacts/win-x64/`。已发布的 `server`、`worker`、`demo` 目录包含运行时，运行时无需另外安装 .NET。请保留三者的相邻目录关系；不要只复制一个 exe。

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

脚本实际调用：

```powershell
claude mcp add --transport stdio --scope project windows_cu -- "C:\实际项目路径\artifacts\win-x64\server\WindowsComputerUse.Server.exe"
```

`project` scope 在当前项目配置 `.mcp.json`；`user` scope 为该用户的 Claude Code 配置。此脚本只注册 MCP。项目里的 Skills 随项目被发现；需要在所有项目使用 Skill 时，将两个 Skill 目录复制到 `%USERPROFILE%\.claude\skills\`。

也可参考 `.mcp.json.example`，将实际路径合并到已有 `.mcp.json` 的 `mcpServers`，保留其他服务器配置。`Build.ps1` 会生成包含本机实际路径的 `artifacts/win-x64/claude-mcp.json` 供参考。

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

Windows 示例闭环（需要 .NET 10 SDK 和交互桌面，先构建）：

```powershell
.\scripts\Test-Windows.ps1
```

该脚本通过真实 MCP stdio 操作它自行启动的测试客户端，验证两次订单查询、强制 WGC 截图、根据 PNG 颜色定位自绘区域并点击、中文与 emoji 输入、快捷键清空、弹窗关闭、移动后旧坐标拒绝、缩放截图点击、最小化恢复及应用关闭重开。WGC 失败会使测试失败，不会用其他截图后端掩盖结果。报告写入 `artifacts/acceptance/windows-smoke.json`，截图为 `demo-capture.png` 和 `demo-moved-scaled.png`。图像定位使用测试画布的已知颜色，**不是 Claude Code 的视觉理解或自主规划测试**。

不操作桌面的协议检查：

```powershell
dotnet run --project tests/WindowsComputerUse.Tests -c Release -- --protocol --server artifacts/win-x64/server/WindowsComputerUse.Server.exe
```

仓库的 `.github/workflows/windows.yml` 在推送 `main`、PR 和手动触发时运行 Windows 构建、通用检查、MCP 协议和严格 WGC 示例测试。报告与截图作为 Actions artifact 保存 14 天，包含测试提交、系统架构和 Windows 会话信息。WGC 或桌面测试失败会使任务失败，不会通过跳过或截图回退标记成功。托管 runner 是否能提供可捕获的交互桌面以实际执行结果为准；CI 通过仍不代表 Claude Code 会话和真实业务客户端验收完成。

## 已实现的接口与边界

见 [接口说明](docs/interfaces.md)。核心行为：

- 窗口引用绑定 HWND、PID 和进程启动时间，窗口重开后重新枚举。
- 控件与截图引用有效期两分钟；同一窗口执行动作后旧观察失效。
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
