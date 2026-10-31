# 窗口材质

[返回架构](architecture.md) · [使用指南](USER-GUIDE.md#5-颜色与玻璃)

LumaDock 的材质由原生背景、自定义光学表面和边缘反光组成。三者各自承担清晰度、明暗和交互反馈，文字与图标保持原生 WPF 渲染。

## 原生背景

`WindowBackdrop.Attach` 在 HWND 创建前附加 `WindowChrome`，保留普通非分层 WPF 窗口。客户区交给 DWM，支持时使用 `DWMWA_SYSTEMBACKDROP_TYPE=38`、`DWMSBT_TRANSIENTWINDOW=3`，即 Desktop Acrylic。

原生 Acrylic 路径需要 Windows 11 build 22621 及以上。旧系统、系统透明效果关闭、高对比度、远程会话及节电等条件下，原生背景按系统支持回退。应用不替用户修改 Windows 个性化或透明设置。

WPF 的背景与 HwndTarget 保持适合原生合成的状态，DWM 负责圆角、阴影和窗口合成。主题、会话、合成与电源通知会合并刷新；相同参数复用已有状态，窗口关闭时移除钩子与会话通知。

## 背景折射与柔化

`LiquidGlassSurface` 获取窗口或 Dock 后方的有限区域，在后台进行曲边取样位移与内部柔化。区域采样失败时，尝试系统提供的可读静态壁纸；无可用来源时保留原生背景或不透明底色。

动态区域采样需要 Windows 10 build 19041 及以上、可用的桌面合成和自身窗口捕获排除能力。受保护内容不保证可取得。采样只临时改变本进程窗口的捕获属性，结束后恢复，不隐藏窗口，也不改变其他进程的属性。

背景像素保存在本地内存，普通运行不写入截图、不进入日志或配置，也不上传。开发诊断模式可能单独保存程序渲染图，分享诊断产物前需要人工检查。

动态透景使用有限频率更新，活动管理窗口或鼠标停留的 Dock 才会持续取样。隐藏、不活跃和空闲 Dock 停止持续更新。同尺寸位图复用，重复背景可以跳过光学渲染。关闭“动态透景”仍可保留打开、移动和尺寸变化等事件触发的更新。

**原生背景的系统回退与自定义折射是两条路径。**关闭 Windows 透明效果不等于关闭所有自定义玻璃；需要停止自定义折射时，请关闭 LumaDock 的“液态玻璃”或启用应用内省电模式。高对比度也会抑制自定义表面。

与第三方截图或录屏并发时，临时捕获排除可能让对方短暂漏掉 LumaDock 窗口。需要稳定截图时，先关闭动态透景并等待当前采样结束。

## 边缘流光

`GlassRim` 使用细高光、内缘与下部暗边呈现厚度。短暂的轮廓反光随真实指针位置变化启动，最后一次输入后约 620 ms 收束；离开后约 180 ms 消退。停稳时不保留连续动画计时器。

流光只移动二维反光，不执行实时流体模拟，不驱动后方画面采样。幅度可调为零，减少动态效果或不活跃时停止。

## 检查

```powershell
.\scripts\test-backdrop.ps1
.\scripts\test-sampler.ps1
.\scripts\test-lens.ps1
.\scripts\test-typography-ui.ps1
```

检查分别覆盖 HWND 与原生材质、已知背景下的自身窗口排除和资源释放、合成图像的取样算法，以及流光有限停止。原生属性或像素算法通过不能代替不同背景、DPI 和主题下的视觉验收；对应记录见 [验证范围](validation.md)。

## 接口资料

- [DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [DwmExtendFrameIntoClientArea](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)
- [WindowChrome.GlassFrameThickness](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome.glassframethickness?view=windowsdesktop-10.0)
