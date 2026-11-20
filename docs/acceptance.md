# Windows 验证记录

## 最新更正：用户级安装后正常使用

2026-11-20，代码 `72cff221847c8b395f87ade4fb65d6e2c88b365a` 的 [Windows CI](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/44696758984) 已完成并通过。

此前默认入口只配置项目 `.mcp.json`，要求在工程目录使用；这属于项目内测试接入。现已将 `Setup-Claude.cmd` 改为正式用户级安装：复制程序到固定目录、用 Claude CLI 注册 user scope MCP、安装两个个人 Skills。安装后可在日常工作目录启动 Claude Code，无须保留原解压目录。

本次安装验收共 3 项：已完成 3、失败/阻塞/待执行 0；均已验证：

1. 首次安装：Windows PowerShell 5.1 执行安装器，程序位于独立安装目录，Claude Code 2.1.329 报告 Connected。
2. 重复安装：同一包再次安装成功，两个个人 Skills 及安装路径元数据正确，无关个人 Skill 的哈希保持不变。
3. 跨目录使用：将原解压目录移走，在另一个无项目配置的工作目录执行 `claude mcp get windows_cu`，仍报告用户级配置及 Connected。

测试使用隔离的 `CLAUDE_CONFIG_DIR` 和含中文/空格的安装路径。只验证 CLI 接入和个人 Skill 文件安装；没有调用 Claude 模型，Skill 自动匹配、业务自主执行仍未验证。Windows Server 2025 的桌面回归也通过：通用 27 项、协议 9 项、WinForms 39 项、WPF 30 项（各组有重复检查）。

证据：[安装与连接 JSON](validation/2026-11-20-user-install/claude-connection.json)、[首次安装](validation/2026-11-20-user-install/claude-install.txt)、[重复安装](validation/2026-11-20-user-install/claude-reinstall.txt)、[其他目录中的连接](validation/2026-11-20-user-install/claude-connection.txt)、[统一回归报告](validation/2026-11-20-user-install/summary.json)、[来源与哈希](validation/2026-11-20-user-install/provenance.json)。

以下保留此前功能扩展基线的完整记录，不能将该基线中的项目级接入说明当作当前默认安装方式。完整需求仍有文末列出的实机/模型/客户端验收未完成。

## 此前功能扩展基线

日期：2026-11-20。被测代码：`95283d648682fa2a4fa2fc7f10adc161874f339b`。

[最终 Windows CI](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/44694395792) 已完成，结果为 success。环境为 **Windows Server 2025 x64，build 26100**，交互会话 ID 2，单显示器 1024×768、窗口 DPI 96。测试入口使用 **Windows PowerShell 5.1.26100.33648**。运行时报告中的 `Windows 10.0.26100` 是系统版本号，不能作为 Windows 10 客户端验收证据。

## 该轮验证

| 项目 | 执行状态 | 验证状态 | 范围 |
|---|---|---|---|
| Windows 构建及通用测试 | 已完成 | 已验证 | 0 警告、0 错误；27 项通用检查通过 |
| 标准 MCP stdio | 已完成 | 已验证 | 9 项：初始化、六个工具 schema、后台模式与截图参数错误 |
| WinForms 原生闭环 | 已完成 | 已验证 | 39 项，包括 9 项公共协议检查；订单、WGC、输入、弹窗、窗口变化与恢复 |
| WPF 原生矩阵 | 已完成 | 已验证 | 4 组、30 项：筛选/等待、UIA pattern、后台/遮挡、原生鼠标 |
| Claude Code CLI 接入 | 已完成 | 已验证 | Claude Code 2.1.329；重复配置保留已有中文路径，隔离用户配置中实际报告 Connected；没有调用模型 |
| 可搬运自包含测试包 | 已完成 | 已验证 | 在含中文和空格的解压路径运行；5 个测试组通过、失败/阻塞/未执行均为 0；包内 SHA-256 校验通过 |
| 导出截图复核 | 已完成 | 已验证 | 已检查该轮四张 PNG：订单 A1002/Query #2、移动后的缩放图、遮挡目标图、WPF 输入事件结果 |

该轮共 7 项验证：已完成 7、正在执行 0、待执行 0、阻塞 0、失败 0；均在上表限定范围内已验证。测试组含重复检查，不能把 27、9、39、30 简单相加称为独立业务场景。

自动入口的 5 组是 preflight、portable、protocol、winforms、wpf-matrix。Claude CLI 检查在后续独立 CI 步骤运行，因此 `summary.json` 中的 `claudeVersion: null` 不表示该后续检查缺失；实际版本和连接结果见 `claude-connection.json`。报告始终保留 `claudeModelVerified: false` / `modelTaskVerified: false`。

## 实际操作证据

WinForms 测试通过标题与 PID 定位，连续查询 A1001/A1002，核对输入、订单、金额、状态和新查询序号；强制 WGC，不接受后端回退。随后从 PNG 已知色块定位 Orange/Blue 并点击，核对新 UIA 结果；验证中文与 emoji 输入、组合键清空、模态弹窗发现与关闭、窗口移动后拒绝旧坐标、缩放截图映射、最小化恢复、worker 重启、应用关闭和重新打开。

WPF 测试实际验证：

- 窗口进程名、标题、PID 联合筛选；返回窗口类名和 DPI。
- AutomationId 与控件类型联合筛选；完整观察与裁剪观察对 absent 的不同判定。
- Unicode 精确值等待、只读错误、异步状态文本、enabled 条件。
- toggle_on/off、选择与多选增删、展开/折叠、子节点出现。
- 滑块 RangeValue=73，报告 0..100 范围，拒绝越界 101。
- ScrollPattern 垂直滚动，以及 ScrollItem 将 Choice30 带入可见区。
- 另一个程序占据前台且完全覆盖目标时，UIA 修改精确文本并从 WGC 取得目标内容。
- 后台阶段 **157 次焦点采样、0 次不匹配，最大采样间隔 26 ms**。这是采样证据，不是连续无干扰保证。
- SendInput 移动、双击、右键、滚轮、短拖拽；从客户端实际事件计数核验。调整窗口大小后拒绝旧截图坐标。

拖拽测试只覆盖一次短输入批次，不证明需要悬停等待的复杂拖放。UIA 模式受客户端实现约束；这两种示例框架通过不代表所有 Windows 软件兼容。已知颜色识别验证坐标链路，不证明模型视觉理解。

## 原始报告与下载

- [统一报告](validation/2026-11-20-windows-ci/summary.json)
- [协议报告](validation/2026-11-20-windows-ci/protocol-smoke.json)
- [WinForms 报告](validation/2026-11-20-windows-ci/windows-smoke.json)
- [WPF 报告](validation/2026-11-20-windows-ci/windows-matrix.json)
- [Claude CLI 连接记录](validation/2026-11-20-windows-ci/claude-connection.json)
- [四张图片与 SHA-256 来源记录](validation/2026-11-20-windows-ci/provenance.json)

图片：[订单结果](validation/2026-11-20-windows-ci/demo-capture.png)、[移动后缩放](validation/2026-11-20-windows-ci/demo-moved-scaled.png)、[遮挡目标](validation/2026-11-20-windows-ci/wpf-occluded.png)、[鼠标事件完成后](validation/2026-11-20-windows-ci/wpf-final.png)。缩放图在点击 Blue 之前拍摄，所以画布仍显示 Orange；Blue 的实际结果由新 UIA 观察核验。

[该轮 Actions 页面](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/44694395792) 的 `windows-cu-win-x64-95283d6...` artifact 包含 Windows 测试 ZIP，保留 30 天。日志和原始验收 artifact 保留 14 天。以上关键 JSON/PNG 已保存在仓库中供长期复查；保留原始报告字节，不修改原始测试结果。

按 [Windows 集中测试说明](windows-testing.md) 搬运包，运行 `Start-Test.cmd`，再用 `Setup-Claude.cmd` 配置 Claude Code。测试器、Server、Worker 和两个示例均自包含 .NET 运行时；目标机无需 SDK。Claude Code 本身仍需安装和登录，模型验收按清单人工执行。

## 尚未完成的完整验收

| 项目 | 执行状态 | 验证状态 | 尚缺条件或检查 |
|---|---|---|---|
| Windows 10/11、更多 UI 框架 | 阻塞 | 未验证 | 本环境只有 macOS 和 Server 2025 CI；需要目标 Windows 10/11 电脑及 Electron/Qt 等实际应用 |
| Claude 模型执行、Skill 自动匹配与新会话复用 | 阻塞 | 未验证 | CLI 连接已验证；尚无已登录并授权模型的 Windows Claude 会话 |
| 真实业务客户端及业务 Skill | 阻塞 | 未验证 | 首个真实客户端和代表性业务任务尚未指定 |
| 多显示器、不同 DPI、HDR、捕获时持续缩放 | 待执行 | 未验证 | 当前只验证单屏 96 DPI、缩放输出、移动/调整大小后的坐标失效与遮挡 |
| 人同时操作键鼠的后台共存 | 待执行 | 未验证 | 当前为测试程序占据前台与焦点采样，尚未执行真实人机并发输入验收 |

剩余 5 类扩展验收：待执行 2、阻塞 3，均未验证。完整安全治理、虚拟化表格实体化、复杂长拖放等不在该轮已交付范围内；安全治理按此前约定后置。**原始完整需求没有全部完成验收。**

历史记录：2026-11-16 的 [WinForms CI](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/43862520352) 及 [原始证据](validation/2026-11-16-windows-ci/provenance.json) 保留作对照。本页的当前结论以 2026-11-20 的最终运行和被测提交为准。
