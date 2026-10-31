# 原生窗口玻璃

LumaDock 保留 Windows DWM 原生背景作为回退。0.4 新增独立的液态层：采样窗口后方区域，在内存中处理曲面像素偏移和内部柔化，活动窗口按上限刷新；空闲 Dock 和不活跃窗口停止刷新。正常运行不保存或上传背景像素，开发诊断模式可以保存程序自身的渲染图。没有 WPF BlurEffect、浏览器或第三方模糊引擎。

`WindowBackdrop.Attach` 在创建 HWND 前附加 `WindowChrome`，保留普通、非分层 WPF 窗口。`Refresh` 将整个客户区交给 DWM，使用 `DWMWA_SYSTEMBACKDROP_TYPE=38` 与 `DWMSBT_TRANSIENTWINDOW=3`。微软在 Windows 11 中将这种材质定义为 Desktop Acrylic；公开支持范围从 Windows 11 build 22621 开始。

窗口的 WPF Background 与 HwndTarget 保持透明，界面只叠加轻度色调和边框。上层不应再绘制整块不透明背景，否则会遮住系统材质。DWM 管理圆角、阴影和合成；当前没有逐帧刷新循环。

在 Windows 10、用户关闭系统透明效果、高对比度、远程会话、系统节电或应用省电模式下，回退到不透明背景。应用不会替用户开启 Windows 透明效果，也不会修改个性化设置或注册表。材质显示也取决于系统策略、显卡驱动及系统合成器。

系统主题、合成、设置、电源或会话变化时合并一次刷新；相同参数的重复刷新直接复用当前状态。窗口关闭时移除本窗口的消息钩子和会话通知。

## 验证

动态透景会在有限区域采样前临时排除自身窗口，再恢复原状态。与第三方截图或录屏恰好并发时，采样方可能短暂漏掉 LumaDock 窗口；本机实机检查已观察到这种情况。需要稳定截图时，可先关闭“动态透景”并等待当前采样完成。它不改变其他程序的窗口属性或系统隐私设置。

```powershell
.\scripts\test-backdrop.ps1
```

该测试在 Windows 会话中创建一个不激活、屏幕外的真实 WPF HWND，核对：非分层窗口、DWM 返回值及读回的材质枚举、透明合成目标、省电时不透明回退、浅色模式下恢复原生材质，以及最大化时是否遵守显示器工作区。它不更改系统设置。

本机 Windows 11 x64 10.0.26300 实测四项通过，`DwmSetWindowAttribute` 返回 `S_OK=0`，读回材质为 3；应用省电时读回 1，WPF 背景不透明；恢复后读回 3。最大化边界通过工作区断言。原始结果在 `artifacts/tests/backdrop-results.json`。

原生属性通过并不单独证明视觉效果足够好。正常窗口的显示、边框、按钮、DPI 和最终透明程度仍需截图及实际交互验收；Windows 10 / 远程 / 高对比度的实际环境仍需另测。

## 微软资料

- [DWM_SYSTEMBACKDROP_TYPE：Desktop Acrylic 与支持版本](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [DwmExtendFrameIntoClientArea：完整客户区玻璃与合成变化](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)
- [WPF WindowChrome.GlassFrameThickness](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome.glassframethickness?view=windowsdesktop-10.0)
