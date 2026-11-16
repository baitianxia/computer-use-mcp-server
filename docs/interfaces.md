# MCP 接口约定

目标客户端：Claude Code CLI。传输：本地 stdio；标准 MCP tool schema、`content` 文本/图片、`structuredContent` 和 `isError`。服务端不提供独立 Agent。

工具成功返回 `{"data": ...}`；错误返回 `{"error":{"code":"...","message":"...","execution":"not_executed|unknown",...}}` 并设置 `isError=true`。图片使用 MCP image content block，元数据同时返回在 data 中。

## 参数形状

`list_windows` 接受顶层 `query`（可省略）和 `limit`（默认 50，最大 200）。query 是标题、进程名、可执行路径的子串过滤。自然语言匹配留给 Claude Code。

其他桌面工具采用顶层 `request` 对象；`stop` 无参数。以下是示意值，引用必须取自工具返回结果：

```json
{"request":{"windowRef":"...","maxDepth":5,"maxNodes":150}}
```

用于 `inspect_window`。深度 0..12，节点数 1..500；树被裁剪时 `truncated=true`。每个节点包含控件名称、类型、AutomationId、状态、物理坐标、支持的 pattern 和可读取文本。文本截断为最多 2000 字符。

```json
{"request":{"windowRef":"...","method":"auto","maxWidth":1600}}
```

用于 `capture_window`。maxWidth 320..3840，只缩小不放大。method 支持：

| method | 行为 |
|---|---|
| auto（默认） | 先 WGC；指定可回退错误时转为 PrintWindow，不使用 screen |
| wgc | 强制 WGC；错误直接返回，不回退 |
| print_window | 直接调用 PrintWindow；部分应用可能返回黑图或旧图 |
| screen | 显式屏幕截图；目标必须已处于前台，可能包含遮挡内容 |

返回 `method`（实际后端）、`requestedMethod`、`imageWidth/imageHeight`、`screenBounds`（截图覆盖的物理屏幕矩形）、`windowBounds`（Win32 窗口矩形）、`boundsSource`。WGC 优先用 DWM 可见窗口边界匹配帧尺寸，避免把不可见边框计入坐标。无法匹配时返回 `capture_geometry_mismatch`，不猜测原点。调用者传截图像素，服务端负责转换，不能再手动乘 DPI；动作前再次检查窗口和截图边界。

WGC 额外返回 `frameSystemRelativeTimeTicks`，表示合成器渲染该帧的 QPC 时间（100 ns 单位，不是 UTC）。每次调用新建会话与帧池，不复用上次请求缓存的图像，最多等待四秒取得图像帧。这个时间不证明应用刚刚更新；仍可能呈现旧业务数据，必须按客户端规则验证。[微软帧时间说明](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime)

`auto` 仅在 `wgc_not_supported/wgc_device_failed/wgc_timeout/wgc_capture_failed` 时回退；`fallbackError` 保留原始错误。权限拒绝、窗口失效、最小化或坐标错误直接返回。WGC/PrintWindow 都失败时，错误消息包含两次失败原因。WGC 保留系统捕获边框，输出 SDR 8-bit PNG；当前不提供 HDR 色彩管理。

```json
{"request":{"windowRef":"...","action":"set_value","mode":"background_only","observationId":"...","elementRef":"e3","text":"A1001"}}
```

用于 `perform_action`。动作：

| 动作 | 目标与附加参数 | 执行方式 |
|---|---|---|
| invoke | observationId + elementRef | UIA Invoke |
| set_value | observationId + elementRef + text | UIA Value；空字符串清空 |
| toggle/select/expand/collapse | observationId + elementRef | 对应 UIA pattern |
| activate | windowRef | 激活目标窗口 |
| click/double_click/move | 元素引用或截图 observationId + x/y | 前台鼠标 |
| drag | 截图 observationId + x/y/toX/toY | 前台短拖拽 |
| scroll | 元素引用或截图坐标；scrollDelta 默认 -120 | 前台滚轮 |
| type_text | text；可选元素引用，否则使用目标窗口现有焦点 | 前台 Unicode 输入 |
| press_keys | keys，如 ["CTRL","A"]；可选元素引用 | 前台组合键 |

`mode` 默认 `foreground_allowed`；`background_only` 拒绝上述全部前台动作。
`button` 为 left/right/middle，默认 left。`observeDelayMs` 0..3000，默认 200，是动作后重新读取 UIA 前的短等待，不代表页面已经稳定。

ActionResult 的 `execution=executed` 只表示动作返回；`verification=not_verified` 表示业务尚未验证。后续 observation 或 warnings 说明当前可读状态。

```json
{"request":{"windowRef":"...","text":"Completed query #1","timeoutMs":5000,"exact":true}}
```

用于 `wait_for`。匹配当前节点的 name/value/text；timeoutMs 为 100..30000，实际结束还可能包含最后一次 UIA 调用的等待时间。predicateMatched 不证明本次业务操作已经完成。

## 引用生命周期

窗口引用在当前 worker 会话中有效，绑定窗口句柄和进程实例。控件/截图引用最多两分钟、最多缓存 32 个观察。同一窗口的动作会使旧观察失效；工具返回的动作后观察是新引用。终止 worker 后所有引用失效。

相同进程内窗口句柄恰好复用等极端情形，首版没有 WinEvent 生命周期跟踪来完全排除。不要把窗口引用当作长期的客户端身份；Skill 使用语义与进程信息重新定位。

## 中止与等待

调用按桌面会话串行执行。`stop` 不进入普通调用队列，可以中止当前 worker。MCP cancellation 也会停止对应调用。进程级超时控制可终止卡住的 UIA/PrintWindow；已经发生的应用行为不能撤回。未知结果要重新观察，不自动重放。

## 当前未覆盖

Windows 10/11 与跨 UI 框架的兼容性验证、窗口生命周期事件、严格用户输入隔离、完整 UIA 模式集合、虚拟化表格查找、剪贴板粘贴、异步业务任务队列及业务审批。Windows Server 2025 的 Demo 原生验证记录见 [验收说明](acceptance.md)。通用的 `wait_for` 不替代具体客户端的业务验证规则。
