# Windows 集中测试

这个包包含 Server、Worker、WinForms/WPF 测试客户端和独立测试器。解压后即可运行，无需 .NET SDK；Claude Code 需另行安装并登录，才能进行模型驱动的任务验收。

## 安装到 Claude Code，再正常使用

1. 先安装 Claude Code，在普通 Windows Terminal 确认 `claude --version` 可用。
2. 完整解压本包，双击 `Setup-Claude.cmd`。它会将程序安装到 `%LOCALAPPDATA%\WindowsComputerUse\versions\...`，注册用户级 MCP，并安装个人 Skills。无需管理员权限。
3. 在你平时的任意工作目录启动或重启 `claude`，使用 `/mcp` 检查 `windows_cu`，然后直接输入桌面任务。不要求进入解压目录，也不要求手动启动 Server。
4. 用以下任务验收。示例 Skill 会从自己的 `installation.json` 找到已安装的演示程序。

```text
/cu-demo-orders 启动订单演示客户端并查询 A1001，核对订单号、状态、金额和新的查询序号。
在 CU Demo Orders 查询 A1002，确认不是上一次的旧结果。
允许前台操作：通过截图定位并点击 Orange 色块，核对选择结果。
在 CU Demo Orders 打开 Show dialog，识别弹窗并关闭，重新观察主窗口。
仅在后台查询 A1001，无法后台完成时停止并说明原因。
```

关闭并重开 Claude Code，从另一个工作目录重复查询，检查个人 Skill 复用。用户级 MCP 和个人 Skills 是 Claude Code 官方支持的跨项目接入方式。[MCP 用户级配置](https://code.claude.com/docs/en/mcp#user-scope)、[个人 Skills](https://code.claude.com/docs/en/skills#choose-where-skills-load)。

安装后的程序路径记录在 `%LOCALAPPDATA%\WindowsComputerUse\installation.json`；解压目录可移动或删除。已有 Claude 会话需要重启加载新 MCP。重复安装保留旧版本程序和同名 Skill 的备份，位置在 `WindowsComputerUse/backups`；其他 MCP/Skill 不由本安装器更改。设置了 `CLAUDE_CONFIG_DIR` 时，Skills 安装在该目录下。

开发者需要项目内调试时，另用 `scripts/Register-ClaudeMcp.ps1 -Scope project`。旧项目的同名 local/project MCP 配置优先于 user，遇到旧路径可用 `claude mcp get windows_cu` 确认作用域，再移除相应旧配置。

随后可让 Claude 从安装目录启动 `wpf/CuWpfDemo.exe`，集中检查新增 UIA 能力：

```text
在 CU WPF Controls 中将 EditableText 改为“本次测试”，打开 Enable option，
同时选中 Choice 01 和 Choice 02，展开 Orders folder，将滑块设为 73，
滚动使 Choice 30 可见。逐项从新的控件状态核对结果；不能执行或无法验证的项单独报告。
```

`templates/claude-client-skill` 可复制为真实客户端 Skill；接口字段见 `docs/interfaces.md`。CLI 连接检查不调用模型，也不替代上述业务验收。自动化的已知色块识别用于检查截图与坐标映射，不证明模型视觉理解。

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

自动诊断可以在安装前或安装后独立运行；正常使用不要求先跑这一步。建议在同一次 Windows 会话保留自动报告和上述人工验收结果。修复后重新运行不会覆盖旧报告。

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
