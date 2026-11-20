---
name: cu-demo-orders
description: 在本项目的 CU Demo Orders Windows 测试客户端查询模拟订单 A1001、A1002，核验查询完成状态，或演示弹窗和画布视觉操作。仅用于 CuDemo 测试客户端。
argument-hint: "[订单号或演示任务]"
---

先读取 `${CLAUDE_SKILL_DIR}/../windows-desktop/SKILL.md`，复用通用观察与动作约定。用户任务：$ARGUMENTS

## 应用识别

- 别名：CU Demo Orders、CuDemo、订单演示客户端。
- 窗口标题包含 `CU Demo Orders`；进程名通常为 `CuDemo`。同时检查页面内的 `Order number` 输入框和 `Query` 按钮。
- 客户端已启动时复用现有窗口。未启动时，先读取 `${CLAUDE_SKILL_DIR}/installation.json`（若存在），使用其中 installDirectory 下的 `demo/CuDemo.exe`。项目调试时再查找解压包的 `demo/CuDemo.exe` 或源码构建目录的 `artifacts/win-x64/demo/CuDemo.exe`。不要把当前工作目录当作安装目录。
- 多个实例无法区分时询问用户，不保存固定 HWND/PID。

## 查询与验证

查找输入框时优先使用名称 `Order number`、控件类型 Edit，以及可用的 AutomationId `OrderNumber`；查找按钮时使用名称 `Query` 和类型 Button。所有实际引用来自当前观察。

输入订单号后发起查询，结果可能在约 750 毫秒后更新。加载时旧结果故意保留，不能把旧结果当成新查询结果。

记录查询前的序号；本次成功需要同时满足：

1. 输入值等于请求订单号。
2. 状态显示新的 `Completed query #N`，N 大于查询前序号。
3. 结果中订单号与请求一致，且结果的 `Query #N` 与完成状态一致。
4. 若用户查询模拟数据，A1001 应为 Ready / 128.00，A1002 应为 Shipped / 256.50。其他订单应报告 Not found。

`wait_for` 可以等待完成状态；最终仍需重新核对序号与订单号。查询时按钮暂时禁用属于正常加载状态。

## 其他演示

`Show dialog` 会打开标题为 `CU Demo Dialog` 的独立模态窗口。枚举并识别该弹窗，关闭后再观察主窗口。

画布中的 Blue / Orange 区域是绘制图形，没有独立按钮控件。截图后按实际位置点击，随后核对 `Canvas selection: Blue` 或 `Canvas selection: Orange`。不保存坐标。

## 验证范围

这是一份按测试客户端源码编写的 Skill；安装文件本身不证明 Claude Code 已在 Windows 中成功使用它。实际验收记录由 `scripts/Test-Windows.ps1` 和人工 Claude Code 场景产生。
