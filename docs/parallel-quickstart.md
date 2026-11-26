# Windows 双桌面并行测试

这个实验包用来验证你在主桌面打字时，Computer Use 能否在另一个 Windows 会话里操作记事本、画图和 Edge。不需要安装 .NET 或配置 WorkBuddy。

1. 在 Windows 10/11 x64 专业版或企业版上把 ZIP **完整解压到新目录**。确认已安装记事本、画图和 Microsoft Edge。
2. 在已解锁的主桌面双击 **`Start-Parallel-Test.cmd`**。如果显示 `Windows child sessions are disabled`，在解压目录打开管理员 PowerShell，运行下面的命令。首次启用后保存工作、注销并重新登录 Windows，再用普通用户双击测试。

   ```powershell
   .\Start-Parallel-Test.cmd -ChildSessionSupport Enable
   ```

3. 等 **Computer Use - child Windows desktop** 窗口打开，在这个窗口里面运行顶部提示的 **`Run-Child.cmd`**。若 Windows 请求登录，请在系统登录窗口完成。
4. 回到主桌面的输入测试窗口，点“开始”，松开鼠标，反复键入屏幕上的六位数字，直到窗口自行关闭。
5. 打开控制台显示的 **`test-results\parallel\本轮编号\REPORT.md`**。看到 `passed` 表示本轮三个应用操作和主桌面输入、焦点及鼠标采样均通过。`failed` 表示检测到应用操作或焦点检查失败。`blocked` 表示某个运行条件未满足。`inconclusive` 表示采样不足以得出结论。

请保留本轮目录里的报告。如果要查看具体错误、恢复系统设置或进一步核对采样，请看[详细说明](START-HERE.md#验证两个桌面同时操作)。
