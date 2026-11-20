---
name: windows-desktop
description: 使用 windows_cu MCP 操作本机 Windows 桌面应用。当用户要求查找桌面窗口、读取客户端界面、点击输入、处理弹窗或通过截图操作自绘控件时使用。代码和文件操作无需使用本 Skill。
argument-hint: "[桌面操作任务]"
---

使用 Claude Code 已连接的 `windows_cu` MCP 完成任务。用户任务：$ARGUMENTS

## 工具与观察

- `list_windows` 的 `query` 是标题/进程/路径子串过滤。可用 `selector` 联合筛选 processName、processId、executablePath、className、titleContains。自然语言匹配由你结合返回信息和客户端 Skill 完成。候选不明确时补充观察或询问用户。
- `inspect_window` 返回 UIA 树及 `observationId`、`elementRef`。可用 selector 按 automationId/name/nameContains/controlType/className 联合筛选，仍受 maxDepth/maxNodes 扫描上限约束。filtered 结果可能不包含父节点；truncated 时不能推断未返回的控件不存在。优先使用已暴露的 UIA pattern，并检查 state 中的选中、切换、展开、只读、数值和滚动状态。
- `capture_window` 默认 `method: auto`，先 WGC，指定错误时回退到 PrintWindow，不会自动激活目标或改为屏幕截图。返回 PNG、图像尺寸、实际 `method`；发生回退时 `fallbackError` 说明原因。显式 `method: wgc` 不回退，可用于 WGC 验收。
- 视觉动作的 `x/y` 是**返回图片中的像素**，由 MCP 转换为屏幕坐标。必须携带该截图的 `observationId`，不要自行添加窗口边框或缩放系数。
- `perform_action` 一次执行一个动作并返回新的 UIA 观察。旧控件引用和旧截图引用在动作后失效。后续动作使用新观察中的引用；需要图像时重新截图。
- `wait_for` 默认检查文本；可配合 selector 等待 exists/absent/enabled/disabled/value_equals/selected/unselected/toggle_on/toggle_off/expanded/collapsed。absent 需要完整观察；incomplete_observation 不能当作已消失。条件匹配不证明业务结果的新鲜度。
- `stop` 中止当前执行进程。已送达的动作不会撤销；再次操作前重新枚举窗口。

## 选择动作

UIA 动作 `invoke/set_value/toggle/select/add_selection/remove_selection/expand/collapse/set_range_value/scroll_pattern/scroll_into_view` 必须携带当前 `observationId` 和 `elementRef`。`set_value` 的空字符串表示清空。根据观察中的 pattern 选择动作；set_range_value 使用 number，先检查上下限和只读状态。scroll_pattern 的 horizontal/vertical 使用 small_increment/small_decrement/large_increment/large_decrement/no_amount，至少一个方向有滚动量；scroll_into_view 需要 scroll_item。

键鼠动作包括 `activate/click/double_click/move/drag/scroll/type_text/press_keys`。`press_keys` 表示一个组合键，例如 `["CTRL","A"]`；`type_text` 使用 Unicode 键盘输入，追加到当前光标位置，不自动清空。可指定观察到的输入框 `elementRef` 来设置焦点。`scrollDelta` 每 120 为一格，负值向下。drag 是按下、移动、释放的一次短输入批次；不支持需要悬停等待的长拖放流程。

视觉点击先调用 `capture_window`，定位图片中的目标，再提交截图编号与坐标。窗口移动、缩放或弹出新页面后重新截图。`capture_geometry_mismatch` 表示后端无法确定截图对应的屏幕矩形，需要重新观察，不自行推算。截图出现黑图、缺失或旧图时，检查实际后端与错误，不猜坐标；若任务允许前台，可先激活窗口再显式使用 `method: screen`。WGC 的新帧时间不证明业务数据是本次查询产生的。

所有请求对象字段使用 camelCase。除 `list_windows` 和 `stop` 外，参数包在 `request` 中，例如：

```json
{"request":{"windowRef":"枚举返回值","action":"set_value","mode":"background_only","observationId":"观察返回值","elementRef":"观察返回值","text":"A1001"}}
```

`background_only` 只执行 UIA pattern，拒绝前台键鼠操作。无法后台完成时说明所需前台动作；不要在后台任务中自行改成 `foreground_allowed`。UIA 本身禁用自动聚焦，但目标应用仍可能自行抢焦；查看返回的 `foregroundChanged` 和警告。

## 完成与恢复

每次关键动作后检查返回的新观察。弹窗可能是另一个顶层窗口，需要重新 `list_windows`。`stale_window/stale_observation/stale_element/stale_capture` 表示需要重新定位或观察。

动作 `execution: executed` 只说明底层调用返回。按客户端 Skill 或用户要求核对业务标识、结果和完成状态；缺少证据时报告“已执行、未验证”。不要用单纯截图时间证明结果是本次操作产生的。

对于 `execution: unknown`，先观察当前状态再决定如何继续；不要直接重复提交类动作。相同问题最多做两次有依据的恢复尝试，仍无法继续则停止并给出工具错误及已知状态。

## Claude Code 接入检查

工具不可见时，请用户通过 `/mcp` 检查 `windows_cu`。正常安装使用 `Setup-Claude.cmd`，注册用户级 MCP 并安装个人 Skills，可在日常工作目录使用。开发调试可显式使用 `scripts/Register-ClaudeMcp.ps1 -Scope project`。同名的 local/project MCP 配置优先于 user，排查时注意旧项目配置。不要将终端输出、配置存在或服务进程启动等同于桌面操作成功。
