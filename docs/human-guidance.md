# 人工辅导与客户端经验

模型在客户端中遇到不会操作的环节时，用户指导一小段，模型重新观察并接着完成原任务，再把有依据的方法保存为 Claude Code 个人 Skill。

## 使用

完整解压 Windows 包，运行 `Setup-Claude.cmd`，在日常目录启动或重启 `claude`。安装器注册用户级 windows_cu，并安装 windows-desktop、windows-guide、wecom-desktop、cu-demo-orders 四个个人 Skills。已学到的 windows-client-* 不随程序版本更新而覆盖。

示例：

```text
操作企业微信，找到联系人“测试同事”。先核对身份，发送我指定的测试消息。
如果不会操作，停下来问我，我可以示范。保存验证有效的方法。
```

也可调用 `/wecom-desktop <任务>` 或 `/windows-guide 我来演示卡住的部分`。模型说明录制就绪后再操作，结束后回复“继续”。`Ctrl+Alt+F12` 可本机停止录制；快捷键、超时和 MCP 重启均不表示用户已交回操作权。

## 证据与交接

文字指导保存用户原话、来源和前后界面。操作示范保存选定主窗口及同进程所属弹窗的鼠标/键盘事件、采样 UIA/截图和前后帧关联。其他窗口及进程外对话框不自动加入。

开始辅导后，共享同一辅导目录的 MCP 拒绝新的 perform_action。已送达动作不能撤销；其他软件和不同辅导目录的 MCP 不受此机制管理，Skill 明确禁止绕过暂停。

文本按键只记录“发生文本输入”，不保存逐键字符；快捷键会被记录。非密码 UIA 值与截图仍可能包含目标应用显示的数据。采样可能错过快速弹窗，位置匹配仅是线索，不能当作固定坐标重放脚本。

## 接口

`guide_task({request})`：

| operation | 参数与行为 |
|---|---|
| start | windowRef、task、blockedReason、mode=text/demonstration、maxSeconds=10..600；返回 session.id |
| status | 可省略 sessionId；找回当前或中断会话 |
| note | sessionId、note、noteSource=user/assistant；保留用户原话与模型解释的区别 |
| resume | 用户交回控制后停止采集并重新观察；重启客户端时可传新的 windowRef |
| checkpoint | 已恢复的 sessionId；采集当前结果供视觉核验 |
| cancel | 结束辅导，保留证据 |

`read_guidance({request})`：sessionId、offset（默认 0）、limit（默认 30，上限 100），返回事件与帧编号；传 frameId 返回对应 UIA 和 MCP PNG。

`save_client_skill({request})`：sessionId、lesson、publish（默认 false）、checks 或 userConfirmationNoteId。lesson 包含 skillName（windows-client-*）、clientName、summary、appliesWhen、parameters、steps（goal/instruction/expectedResult/selector）、successCriteria、recovery。由 Claude 根据证据整理，MCP 不调用模型。

- publish=false 保存未安装的 lesson-draft.json。
- publish=true + checks 在恢复后的目标窗口执行 1..10 个新的 WaitRequest，全部匹配才安装。
- 自绘结果无法用 UIA 验证时，先 checkpoint，再让用户结合截图确认。记录实际用户回复，传其 userConfirmationNoteId 发布；确认必须晚于恢复和对应截图，来源标为人工确认。
- 这些证据不证明整个流程已被模型独立复现；聊天发送还需核对对象、正文、发送前后对照和可见状态。

## 保存与复用

- 原始辅导：`%LOCALAPPDATA%\WindowsComputerUse\guidance\<sessionId>\`，可用 WINDOWS_CU_GUIDANCE_DIR 覆盖。
- 个人 Skill：`%USERPROFILE%\.claude\skills\windows-client-<name>\SKILL.md` 与 lessons，尊重 CLAUDE_CONFIG_DIR。
- 备份放在 Claude 配置目录下 windows-cu-skill-backups，不会作为重复 Skill 扫描。
- 同一辅导重试保存更新原 lesson；不同辅导累积。已有手动修改或同名非托管 Skill 时拒绝覆盖。

新会话用 `/windows-client-<name> <任务>` 验证复用。Skill 文件安装与模型实际选用、独立完成任务须分别验证。

## 首期环境和限制

目标 Windows 10 x64，最低 build 19041（Windows 10 2004），当前登录且未锁定。企业微信提供任务 Skill 和辅导入口，真实客户端版本、账号及聊天发送仍需目标电脑验收。测试包不预填真实联系人，也不自动发送真实消息。

录制最多 600 秒、1000 个事件、60 帧，队列上限 256。达到限制只停止采集，控制权仍暂停。丢事件/缺帧须明确说明。不处理 UAC、安全桌面或锁屏。

Windows 自动测试通过测试程序注入输入验证链路，标记 injected；不代表真实人工、Claude 模型或企业微信业务已验收。

规范：[Claude Code Skills](https://code.claude.com/docs/en/skills)、[Microsoft 鼠标 Hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)、[键盘 Hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)。Hook 回调只过滤入队，UIA/截图在独立线程处理。
