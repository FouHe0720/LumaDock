# LumaDock 0.3.0

Windows x64 便携预发布版。解压整个 `LumaDock-0.3.0-win-x64.zip` 后运行 `LumaDock.exe`，包含 .NET Desktop Runtime，无需安装 IDE 或 SDK。本机桌面快捷方式继续指向 `D:\LumaDock\artifacts\publish\LumaDock.exe`。

## 字体跟随 Windows 的 Icon 设置

“设置 → 应用 → 跟随 Windows 图标字体”默认开启。应用和桌面快捷方式的名称读取当前 Windows 图标标题字体，跟随字体、字号、粗细、斜体、下划线和删除线；Dock 的 WPF 名称提示同步字体、字号、粗细和斜体。导航、搜索框和设置文字保持界面字体，不会把整个 Windows 或 LumaDock 的所有文字改成 Icon 字体。

使用 changesize（System Font Size Changer）时：

1. 选择 **Icon**，调整字体设置，点击 **Apply**。
2. 待设置实际应用到 Windows 后，切回 LumaDock，或关闭再重新打开应用窗口。
3. 若未更新，进入“设置 → 应用”，在“系统图标字体”行点击“刷新字体”。如外部工具要求注销，先按其提示完成。

LumaDock 只读取系统值，不替用户更改 Windows 字体或注册表。本机本轮实际读到 **Microsoft YaHei UI（微软雅黑 UI）、15 DIP（11.25 磅）、字重 700（粗体）**；其他电脑按其自身设置读取，不把这一数值写死。

名称变大时会增加两行文字的高度，并自动减少列数。“每行应用数”是上限，窗口较小或字号较大时实际列数会降低。关闭“跟随 Windows 图标字体”后，可单独调整“应用名称字号”和“应用名称加粗”；手动范围为 10–40 DIP（界面显示 7.5–30 磅），不改变 Windows 的字体。

## 玻璃边缘与反馈

新增自行实现的 `GlassRim`：静态内外细边缘、顶部柔和反光和下缘暗部；同时降低表面色调的遮挡。应用卡片悬停最多上移 2 DIP，约 120 ms 后结束，按动画与省电设置减少反馈。边缘不使用持续帧循环。

本轮按用户提供的 `D:\GlassDesk` 只读研究了边缘层次和悬停反馈。参考源码中的部分原创新增内容采用 PolyForm Noncommercial 许可，历史和第三方部分另有许可范围；LumaDock 没有复制该项目的源码、素材或着色器。当前材质仍由 Windows DWM 原生 Desktop Acrylic 合成，没有引入桌面捕获或折射着色器。

Windows 11 22621 及以上可使用原生材质；Windows 10、系统关闭透明效果、远程会话、高对比度或省电条件下保留不透明回退。软件不会代用户开启系统透明效果。

## 已取得的验证

本轮在 Windows 11 x64 10.0.26300、.NET 10.0.14 中完成以下检查：

| 检查 | 结果 | 原始记录 |
| --- | --- | --- |
| Core / Storage | 22/22 通过 | `artifacts/tests/results.json` |
| 自适应网格 | 7/7 通过 | `artifacts/tests/layout-results.json` |
| 系统字体只读集成 | 5/5 通过，与独立 WPF 系统字体快照一致 | `artifacts/tests/font-results.json` |
| 实际 WPF 字体与布局 | 6/6 通过，涵盖当前系统字体、手动 28/40 DIP、桌面名称及恢复系统字体 | `artifacts/tests/typography-ui-results.json` |
| 窗口生命周期 | 100 次显示/搜索/导航/隐藏、3 次重建及关闭动画中重新显示通过 | `artifacts/tests/ui-results.json` |
| 原生玻璃属性 | 4/4 通过 | `artifacts/tests/backdrop-results.json` |
| 单实例激活 | 6/6 通过 | `artifacts/tests/activation-results.json` |

所有字体测试均未修改 Windows 的字体设置；大字号只在隔离的 LumaDock 测试配置中切换。真实 WPF 测试检查控件字体和布局边界，并不等于已经完成外部工具的操作及视觉检查。具体读取方式见 [系统字体说明](SYSTEM-FONTS.md)。

## 尚待验收

截至 2026-10-31，本轮会话的 Windows 桌面处于锁屏状态。需要用户解锁后继续检查浅色/深色的实际透明程度、文字对比度、悬停反馈及 Dock 定位；当前不能声称视觉已经达到最终验收标准，也未进行与 GlassDesk 的实际观感对照。

changesize 的 **Icon → Apply → LumaDock 自动更新**尚未完成端到端测试。已通过的是读取当前真实系统字体，以及在 WPF 中切换隔离配置的布局验证，不是实际修改系统字体后的完整流程。

0.3 的空闲 CPU、私有工作集和启动台延迟尚未测量。需要在解锁的正常桌面会话中对最终发行包采样；[性能记录](performance.md) 中的 0.2 / 0.1 数据属于历史版本，不能当作本版结果。Windows 10、真实混合 DPI 双屏、独占全屏游戏和过夜驻留仍未完成专项验收。

安装依赖、添加应用、Dock、双屏、自启、退出卸载及恢复桌面图标的方法见 [README](../README.md)。默认不开启开机启动、不隐藏桌面图标，也不移动或删除原始文件。当前没有代码签名、自动更新、安装器或桌面时钟。
