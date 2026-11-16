# Windows 验证记录

日期：2026-11-16。被测代码提交：`f7016ca31d4219d6d8f9662b9a852dd57d98d4de`。

[Windows CI 运行](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/43862520352) 已完成，结论为 success。运行环境为 GitHub 托管的 **Windows Server 2025 x64，build 26100**，.NET SDK 10.0.404，交互会话 ID 2。JSON 中的 `Microsoft Windows 10.0.26100` 是运行时版本描述，不能据此称为 Windows 10 客户端验收。

## 本轮已完成的验证

| 项目 | 执行状态 | 验证状态 | 实际证据与范围 |
|---|---|---|---|
| Windows 构建与 x64 自包含发布 | 已完成 | 已验证 | Core、Server、Worker、Demo、测试程序；0 警告、0 错误 |
| 通用逻辑与进程检查 | 已完成 | 已验证 | 23 项通过；回退策略、坐标映射、引用、输入结构与进程中止等逻辑 |
| MCP stdio 协议检查 | 已完成 | 已验证 | 9 项通过；initialize、六个工具 schema、后台动作和截图参数错误 |
| Windows 原生 Demo 闭环 | 已完成 | 已验证 | 39 项检查通过，包含 9 项公共协议检查；原生覆盖范围见下文 |
| 导出图像复核 | 已完成 | 已验证 | 已查看原始与 640 像素宽截图；均有正常目标窗口内容，并可核对 A1002 与 Query #2 |

本轮共 5 项验证：已完成 5、正在执行 0、待执行 0、阻塞 0、失败 0。各测试组有重复检查，不能相加后称为独立业务场景数量。此前 macOS 协议探针有 10 项检查，多出的 1 项是非 Windows 平台拒绝测试。

原生 Demo 已实际执行：

- 通过窗口标题与进程 PID 定位，读取 UIA 控件。
- 用 UIA `set_value` 和 `invoke` 查询 A1001、A1002；核对订单号、状态、金额和新的查询序号。
- 强制 `method: wgc` 获取 PNG，不接受 PrintWindow 回退；验证实际后端、帧时间与 DWM 边界来源。
- 从返回 PNG 中定位已知 Orange 色块，经截图坐标转换和 `SendInput` 点击，核对 `Canvas selection: Orange`。
- 旧截图引用在动作后失效；窗口移动后，旧截图坐标在发送输入前被拒绝。
- 从移动后的窗口获取 640 像素宽 WGC 图像，定位 Blue 色块并点击，核对 `Canvas selection: Blue`。
- 通过原生键盘输入 `测试订单😀`，核对完整 Unicode 值；用 Ctrl+A、Backspace 清空输入框。
- 点击打开模态弹窗，识别 owner 和 UIA 文本；用 Alt+F4 关闭并重新读取主窗口。
- 最小化窗口截图被明确拒绝，恢复后可重新进行 WGC 捕获。
- 停止 worker 后拒绝旧窗口引用，重新发现窗口；关闭并重开 Demo 后使用新引用读取初始状态。

两次 UIA 查询的前后检查点未发现前台窗口变化。这只证明检查点结果，不能证明有人同时使用其他应用时全过程无干扰。

## 可复查证据

- [原始桌面测试报告](validation/2026-11-16-windows-ci/windows-smoke.json)
- [原始协议测试报告](validation/2026-11-16-windows-ci/protocol-smoke.json)
- [查询完成后的 WGC 图像](validation/2026-11-16-windows-ci/demo-capture.png)
- [移动窗口后的缩放图像](validation/2026-11-16-windows-ci/demo-moved-scaled.png)
- [运行编号、源提交与 SHA-256](validation/2026-11-16-windows-ci/provenance.json)

缩放图像拍摄在点击 Blue **之前**，因此图中显示 `Canvas selection: Orange`；Blue 点击后的结果由报告中的新 UIA 观察验证。原始 JSON 保留测试程序输出，没有将后续图像复核改写进报告。

完整构建、桌面执行及基础动作日志保存在该 Actions 运行的 artifact 中，平台保留期为 14 天。仓库中的上述 JSON 和 PNG 提供长期可查的关键证据。初次基础测试运行是 [43860605191](https://github.com/baitianxia/computer-use-mcp-server/actions/runs/43860605191)，本页以扩展后的最新测试为准。

## 尚未完成的验收

| 项目 | 执行状态 | 验证状态 | 尚缺条件或覆盖 |
|---|---|---|---|
| Windows 10/11 与不同 UI 框架兼容性 | 阻塞 | 未验证 | 当前只有 Server 2025 / WinForms Demo 的执行证据，需要对应客户端环境 |
| Claude Code CLI 真实会话与 Skill 复用 | 阻塞 | 未验证 | 尚无已接入并授权模型的 Windows Claude Code 会话 |
| 连续后台不干扰用户输入 | 待执行 | 未验证 | 需连续跟踪前台和输入事件，并测试用户同时操作其他应用 |
| 多显示器、不同 DPI、遮挡、捕获期间缩放 | 待执行 | 未验证 | 当前只验证已有窗口移动、图像缩小和最小化恢复，不能替代这些场景 |
| 其他动作与 UIA pattern 的原生矩阵 | 待执行 | 未验证 | 双击、拖拽、滚动、toggle/select/expand/collapse 等仍需逐项实测 |
| 真实业务客户端与业务 Skill 验收 | 阻塞 | 未验证 | 首个真实客户端及任务尚未指定 |

剩余 6 项扩展验收：待执行 3、阻塞 3；均未验证。颜色定位是已知测试画布的确定性检查，不证明 Claude Code 的视觉理解、自然语言应用匹配或自主决策能力。

当前 Git 初始化、公开推送和上述 Windows CI 测试已完成；**原始完整需求的验收没有全部完成**。完整安全治理、复杂虚拟化控件等也仍在已约定的后续范围内。
