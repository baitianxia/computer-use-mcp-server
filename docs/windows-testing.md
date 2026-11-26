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

新增人工辅导请按 [人工辅导说明](human-guidance.md) 操作。首期现场验收为 Windows 10 x64 上的企业微信：指定测试联系人和消息，遇到卡点示范，保存 windows-client-wecom 后在新会话复用。内置 wecom-desktop 尚未证明真实聊天成功。

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
| guidance | 文字指导、操作权暂停、注入输入示范、所属弹窗、无关窗口排除、本机停止、限时录制、重启恢复、Skill 草稿/发布/累积及人工图像确认 |
| wpf-matrix | 控件筛选与状态等待、只读保护、多选、展开折叠、滑块、滚动、遮挡捕获、后台焦点采样、双击、右键、滚轮、短拖拽及调整窗口大小 |

自动诊断可以在安装前或安装后独立运行；正常使用不要求先跑这一步。建议在同一次 Windows 会话保留自动报告和上述人工验收结果。修复后重新运行不会覆盖旧报告。

## 验证两个桌面同时操作

本实验在同一台 Windows 上开一个子会话。你在主桌面输入数字，本项目的 MCP 执行器在子会话操作三个真实应用。建议先用已解锁的 Windows 10/11 x64 专业版或企业版，确认装有记事本、画图和 Microsoft Edge。完整解压**新版** ZIP 到本地可写目录。可单独运行这个实验，无须先配置 WorkBuddy 或 Claude Code。[微软子会话说明](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)

**安排人工并行测试前**，发布方先在 Windows CI 用同一份解压包执行 `Connect()`，核对 RDP 已连接、Windows 返回的子会话 ID 有效且与主会话不同，再关闭预览。成功记录在 CI 验收附件的 `rdp-child-connection.json`，同目录的 `archive.sha256` 标识被测 ZIP。会话检查阻塞或失败时，CI 保留诊断并停止发布。通过后再安排人在主桌面打字，核验实际应用操作和输入隔离。

连接检查会在启用子会话、显示预览、调用 ActiveX、核验 ID 和关闭预览前更新 JSON。若文件停在 `in_progress`，先看 `stage`、`hostOperation` 和 `lastHostObservation`，再打开同目录的 `rdp-child-connection.supervisor.json`。在刚启用功能的 GitHub 托管运行器上，如果探针连续观察到前台系统凭据窗口，RDP 仍在连接且 Windows 尚未分配子会话，监督报告会记录 `blocked / interactive_credentials_required`（退出码 2）。窗口所属进程不明确时会观察到时限。无明确原因的超时保留退出码 124。两种情况都保留连接阶段与事件。通过的连接报告必须是 `passed`，且 `rdpWtsBindingVerified` 和 `previewClosed` 都为真。

在独立 CI 分支，若运行器原有的桌面连接受阻，测试会在已开启子会话的同一台运行器上创建一次性普通测试账号，使用已有的本机 RDP 监听器验证新登录的父桌面。外层客户端核对本机主机名解析到的每一个地址，并要求 Windows 成功验证服务器身份。诊断文件只记录监听器证书校验状态及本轮测试身份的登录事件类型和状态码。测试要求外层父桌面、原运行器桌面和子桌面的会话 ID 两两不同。核对内部报告、脚本哈希和预览关闭结果后，才可判定该路径通过。外层测试结果在 `fresh-parent.json`，内部检查点在 `fresh-parent.inner.json`。

若 Windows 在建立测试父桌面前报告服务器身份警告，独立分支另有测试模式。它为创建父桌面的客户端进程提供本机 RDP 监听器绑定的公开证书，仅连接本机，要求 TLS 和 NLA 验证。测试先检查错误证书能否被拒绝，再尝试登录。Windows 的证书存储和交付包里的内层 RDP 控件使用原配置。CI 留存本轮外层客户端的软件包来源，以及内层使用的连接脚本哈希。测试还会用公开样本文字比对 RDP 客户端收到的管道字节，并在发起 RDP 之前单独校验本轮测试账号。登录审计只统计 RDP 尝试时段内的事件。

1. 在主桌面双击 **`Start-Parallel-Test.cmd`**。控制台会打印本轮报告路径。如果报告为 `blocked` 并提示 `Windows child sessions are disabled`，普通双击到此结束。要在自己的测试机上启用这项 Windows 设置，在解压目录打开**管理员 PowerShell**，运行 `.\Start-Parallel-Test.cmd -ChildSessionSupport Enable`。**首次启用后，先保存工作，再注销并重新登录 Windows**，然后用普通用户双击测试入口。使用智能卡或在本次登录后才启用子会话时，Windows 可能要求登录。若出现系统凭据窗口，只在 Windows 自己的登录界面输入凭据。设备策略不允许启用时，保留阻塞报告。[微软子会话登录说明](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)
2. 等标题为 **Computer Use - child Windows desktop** 的窗口连接。在**这个窗口里面**打开文件资源管理器，按预览顶部给出的路径运行本轮 `Run-Child.cmd`（在 `test-results\parallel\本轮编号\` 内）。如果从主桌面误点，脚本会提示会话不符，可以回到子窗口重新打开。请保持子窗口连接；若以前的实验仍在这个子桌面打开了记事本、画图或 Edge 测试窗口，先关闭旧测试窗口。
3. 回到主桌面的 **Computer Use | 主桌面输入测试**，点“开始”后松开鼠标。六位数字出现后，用键盘反复输入这组数字，输满六位会自动清空。保持鼠标和触控板不动，持续打字直到窗口自己关闭。脚本约每 50 毫秒采样一次；检测到鼠标偏离起点超过 2 个物理像素时，本轮鼠标隔离证据不足。子桌面会输入并保存带本轮编号的文本、在彩色 PNG 上拖出线条并保存，再点击临时 Edge 配置打开的本地网页。实际结果分别从保存的文本、PNG 像素和点击后的网页标题核对。
4. 打开控制台打印的 **`test-results\parallel\本轮编号\REPORT.md`**。`passed` 表示三个应用均核验成功，且在子程序调用键鼠期间记录到了正确的主桌面输入和足够的焦点样本。`failed` 表示观察到焦点丢失或应用检查失败。`blocked` 表示系统、会话或应用启动条件阻塞。`inconclusive` 表示手动输入、采样覆盖或运行时长不足，或测试已取消。详细证据在 `summary.json`、`focus.json` 和 `parallel-apps.json`（运行到对应阶段才生成）。

测试文件按轮次保存在报告目录。Edge 使用独立的临时配置目录 `%TEMP%\CU-本轮编号-Edge`。关闭测试预览会断开子会话。若要撤回自己启用的系统设置，可再次打开测试预览，进入**子桌面**，保存工作并在里面注销。随后在主桌面的管理员 PowerShell 中运行 `.\Start-Parallel-Test.cmd -ChildSessionSupport Disable`。一次通过仅覆盖这三个操作、这一台主机和本轮采样时段。WorkBuddy 模型调用、操控主桌面已有窗口及其他软件的兼容性需分别验证。

关闭系统子会话功能前，脚本要求 Windows 明确返回“无子会话”。如果此时查询报错，脚本保留当前设置，错误信息中会标注 `disable-check` 和 Win32 错误码。

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
