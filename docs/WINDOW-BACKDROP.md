# 窗口材质

[返回架构](architecture.md) · [使用指南](USER-GUIDE.md#5-颜色与玻璃)

LumaDock 的管理窗口与 Dock 使用不同的合成路径。管理窗口保留原生背景，Dock 为图标放大预留逐像素透明空间；两者都可叠加本地光学表面和短时反光，文字与图标保持 WPF 渲染。

| 窗口 | 窗口合成 | 玻璃与回退 |
| --- | --- | --- |
| 应用库、桌面集、偏好设置 | 普通非分层 HWND，`AllowsTransparency=false` | DWM 原生背景上叠加自定义折射；按系统条件回退 |
| Dock | 小型分层 HWND，`AllowsTransparency=true` | 底座区域采样、轮廓裁切与矢量边缘；无采样或关闭效果时使用底色 |

## 原生背景

管理窗口的 `WindowBackdrop.Attach` 在 HWND 创建前附加 `WindowChrome`，保留普通非分层 WPF 窗口。客户区交给 DWM，支持时使用 `DWMWA_SYSTEMBACKDROP_TYPE=38`、`DWMSBT_TRANSIENTWINDOW=3`，即 Desktop Acrylic。此入口不用于 Dock，也不接受分层透明窗口。

原生 Acrylic 路径需要 Windows 11 build 22621 及以上。旧系统、系统透明效果关闭、高对比度、远程会话及节电等条件下，原生背景按系统支持回退。应用不替用户修改 Windows 个性化或透明设置。

WPF 的背景与 HwndTarget 保持适合原生合成的状态，DWM 负责圆角、阴影和窗口合成。主题、会话、合成与电源通知会合并刷新；相同参数复用已有状态，窗口关闭时移除钩子与会话通知。

## Dock 底座

Dock 将底座和图标活动区分开。底座有胶囊、卵石、轨道三种轮廓，后方采样按轮廓裁切；静态矢量细边、内缘、窄阴影和强调色嵌线补充厚度。图标放大后可向桌面内部抬起，周围留白保持透明。

Dock 不使用原生 Acrylic 作为整个窗口的背景。异步采样首帧到达前保留底色，关闭“液态玻璃”、开启应用内省电或高对比度时使用简单表面。主题色、嵌线与图标底衬可使用潮蓝、青瓷、琥珀、烟紫、玫瑰或石墨；配色不会向 Windows 写入主题设置。

## 背景折射与柔化

`LiquidGlassSurface` 获取窗口或 Dock 底座后方的有限区域，在后台进行曲边取样位移与内部柔化。输出表面最多约 36 万像素，同尺寸复用一个 `WriteableBitmap`。区域采样失败时，尝试系统提供的可读静态壁纸；无可用来源时保留管理窗口原生背景或 Dock 底色。

动态区域采样需要 Windows 10 build 19041 及以上、可用的桌面合成和自身窗口捕获排除能力。受保护内容不保证可取得。采样只临时改变本进程窗口的捕获属性，结束后恢复，不隐藏窗口，也不改变其他进程的属性。

背景像素保存在本地内存，普通运行不写入截图、不进入日志或配置，也不上传。开发诊断模式可能单独保存程序渲染图，分享诊断产物前需要人工检查。

动态透景只在管理窗口活动或鼠标停留 Dock 时持续取样，并受减少动画及高对比度等条件限制。隐藏、不活跃和空闲 Dock 停止持续更新。稀疏场景指纹未变化时跳过光学渲染，并逐步减少后续检查：

| 连续不变的检查次数 | 下次检查间隔 |
| --- | --- |
| 0–2 次 | 120 ms |
| 3–5 次 | 250 ms |
| 6–8 次 | 500 ms |
| 9 次及以上 | 1000 ms |

检查发现变化后恢复到 120 ms；移动、调整尺寸、激活和相关背景事件也会重置节奏。每次请求另有 80 ms 合并节流。因此静态背景退避后，新变化可能等待约 1.1 秒才进入处理，实际显示时间还包含调度、采样与渲染耗时。该表面不提供视频级实时透景。

关闭“动态透景”仍保留打开、移动和尺寸变化等事件触发的更新。关闭“液态玻璃”或启用应用内省电才会释放自定义采样表面。

**原生背景的系统回退与自定义折射是两条路径。**关闭 Windows 透明效果不等于关闭所有自定义玻璃；需要停止自定义折射时，请关闭 LumaDock 的“液态玻璃”或启用应用内省电模式。高对比度也会抑制自定义表面。

与第三方截图或录屏并发时，临时捕获排除可能让对方短暂漏掉 LumaDock 窗口。需要稳定截图时，先关闭动态透景并等待当前采样结束。

## 边缘流光与图标动画

`GlassRim` 使用细高光、内缘与下部暗边呈现厚度。短暂的轮廓反光随真实指针位置变化启动，最后一次输入后约 620 ms 收束；离开后约 180 ms 消退。停稳时不保留连续动画计时器。

流光只移动二维反光，不执行实时流体模拟，不驱动后方画面采样。幅度可调为零，减少动态效果或不活跃时停止。

Dock 的静态边缘由 `DockPlate` 统一绘制，`GlassRim` 只负责短反光，并使用同一底座轮廓。轮廓展平与弧长表只在布局或形态变化时更新；卵石的交错圆角也沿实际路径，不叠加统一圆角的第二圈边。

Dock 图标使用另一套有限插值计时器，目标间隔为 16 ms。鼠标位置更新缩放、抬起与邻近图标位移目标，收敛后停止；离开后回落，隐藏、重建与关闭时清理。背景检查间隔不决定图标动画频率。16 ms 是调度参数，实际流畅度需要在具体显示器和硬件上验收。

## 检查

```powershell
.\scripts\test-backdrop.ps1
.\scripts\test-sampler.ps1
.\scripts\test-lens.ps1
.\scripts\test-cadence.ps1
.\scripts\test-typography-ui.ps1
.\scripts\test-dock-ui.ps1
```

检查分别覆盖管理窗口 HWND 与原生材质、已知背景下的自身窗口排除和资源释放、合成图像的取样算法、静态场景检查退避，以及流光有限停止和设置持久化。原生属性或像素算法通过不能代替不同背景、DPI 和主题下的视觉验收；对应记录见 [验证范围](validation.md)。

## 接口资料

- [DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [DwmExtendFrameIntoClientArea](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)
- [WindowChrome.GlassFrameThickness](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome.glassframethickness?view=windowsdesktop-10.0)
