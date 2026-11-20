# Windows 集中测试

这个包包含 Server、Worker、WinForms/WPF 测试客户端和独立测试器。解压后即可运行，无需 .NET SDK；Claude Code 需另行安装并登录，才能进行模型驱动的任务验收。

## 一次操作收集自动测试结果

1. 将 `windows-cu-win-x64.zip` 完整解压到本地目录，例如 `C:\Tools\Windows CU`。保留所有子目录；支持路径中包含空格和中文。
2. 使用 Windows 10 build 19041 及以上或 Windows 11 x64，登录普通用户桌面，保持解锁。关闭自己手动打开的 CU 测试客户端。
3. 双击 `Start-Test.cmd`。测试会启动并操作它自己的两个演示程序，有前台鼠标键盘检查，运行期间暂时不要操作键鼠。无需管理员权限。
4. 查看 `test-results/<时间>/REPORT.md` 和 `summary.json`。把同名 ZIP 保留下来，用于集中反馈；其中包含各组 JSON、日志和 WGC 截图。

报告将“通过、失败、阻塞、未执行”分开。某组失败后仍尝试运行其余独立组；失败组内依赖前一步的检查可能未运行。报告中的 `checks` 只包含实际执行的检查，不把缺少的检查算作通过。

自动检查包括：

| 测试组 | 内容 |
|---|---|
| preflight | Windows/交互会话及包内每个文件的 SHA-256 |
| portable | 参数、选择器、引用、坐标、错误、超时和中止 |
| protocol | 真实 stdio MCP 初始化、工具 schema 和错误返回 |
| winforms | 订单新旧结果核验、WGC、坐标点击、Unicode、快捷键、弹窗、窗口移动、最小化及重开 |
| wpf-matrix | 控件筛选与状态等待、只读保护、多选、展开折叠、滑块、滚动、遮挡捕获、后台焦点采样、双击、右键、滚轮、短拖拽及调整窗口大小 |

首次测试建议保留完整自动报告，然后在同一次 Windows 会话继续下面的人工检查。修复后重新运行不会覆盖旧报告。

## Claude Code 接入和模型验收

1. 在普通 Windows Terminal 中确认 `claude --version` 可用。
2. 双击 `Setup-Claude.cmd`。它按当前解压路径创建/更新项目 `.mcp.json`，保留其他 MCP 配置，并在覆盖旧配置前保存备份。移动解压目录后重新运行一次。
3. 在该解压目录启动 `claude`。按 Claude Code 提示处理项目/MCP 信任，使用 `/mcp` 确认 `windows_cu` 已连接。脚本不修改全局权限模式。
4. 打开 `demo\CuDemo.exe`，在 Claude Code 输入以下任务，完成后保存对话中的操作事实和核验结果。

```text
/cu-demo-orders 查询 A1001，核对输入值、订单号、状态、金额和新的查询序号。
在 CU Demo Orders 查询 A1002，确认不是上一次的旧结果。
允许前台操作：通过截图定位并点击 Orange 色块，核对选择结果。
在 CU Demo Orders 打开 Show dialog，识别弹窗并关闭，重新观察主窗口。
仅在后台查询 A1001，无法后台完成时停止并说明原因。
```

关闭并重新启动 Claude Code，再重复一次自然语言订单查询，检查 Skill 是否在新会话中自动匹配。`.claude/skills` 是 Claude Code 项目 Skill 目录；`name`、`description` 和 `$ARGUMENTS` 使用其官方格式。[MCP 规范](https://code.claude.com/docs/en/mcp)、[Skills 规范](https://code.claude.com/docs/en/skills)。

随后打开 `wpf\CuWpfDemo.exe`，集中检查新增 UIA 能力：

```text
在 CU WPF Controls 中将 EditableText 改为“本次测试”，打开 Enable option，
同时选中 Choice 01 和 Choice 02，展开 Orders folder，将滑块设为 73，
滚动使 Choice 30 可见。逐项从新的控件状态核对结果；不能执行或无法验证的项单独报告。
```

包内 `templates/claude-client-skill` 可复制为真实客户端 Skill；接口字段见 `docs/interfaces.md`。只填写已经观察或核实的控件特征，不保存固定句柄或坐标。

CI 的 CLI 连接检查不调用模型，也不替代上述步骤。自动化的已知色块识别用于检查截图与坐标映射，不证明模型的视觉理解。

## 同一次测试再记录这些信息

- Windows 版本/构建、架构、显示器数量和缩放比例。自动报告记录运行环境和虚拟屏幕；具体显示配置请补充。
- 首个真实客户端名称/版本，以及一个典型任务、期望结果和失败信息。
- 在该客户端中至少核验一次目标识别、输入/操作、结果标识、新结果完成状态。
- 有多屏/DPI/HDR设备时再测相应显示场景；没有对应设备应记“未执行”。后台焦点采样不等同于与人同时使用键鼠全过程无干扰。

## 命令行和源码

```powershell
# 解压包内执行，不需要 dotnet
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Windows.ps1

# 源码目录构建，需要 .NET 10 SDK；随后同样使用独立测试器
.\scripts\Build.ps1
.\scripts\Test-Windows.ps1
```

`Start-Test.cmd` 的 ExecutionPolicy 参数只作用于当前 PowerShell 进程。若计算机策略禁止脚本，应按现有策略处理，不能据此跳过自动验收。

遇到 `foreground_not_target/input_busy` 时，保持桌面解锁、释放键鼠并重跑；不要对不确定的业务操作自动重复。管理员应用、UAC、安全桌面和锁屏不在本包的自动操作范围内。
