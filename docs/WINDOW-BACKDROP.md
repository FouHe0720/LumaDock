# 窗口材质

[返回架构](architecture.md) · [使用指南](USER-GUIDE.md#5-颜色与玻璃)

动态透景和静态折射使用两条独立路径。默认动态模式以清透风格保留壁纸细节，由 Windows 合成后方画面，也可选择原生磨砂；静态模式按窗口事件取得一张背景，再进行曲边折射。文字、图标和边缘反光保持 WPF 渲染。

| 模式与窗口 | 合成方式 | 背景行为 |
| --- | --- | --- |
| 动态清透：应用库、桌面集、偏好设置 | 普通非分层 HWND，扩展透明客户区与半透明底色 | 保留后方细节，由系统合成，不覆盖应用采样帧 |
| 动态磨砂：上述管理窗口 | 普通非分层 HWND，DWM Desktop Acrylic | 柔化后方画面，不覆盖应用采样帧 |
| 动态：Dock | 小型分层 HWND，真实半透明底座 | 系统合成底座后方画面，矢量边缘独立绘制 |
| 静态：两类窗口 | 有限区域采样与 CPU 光学图像 | 打开、激活、移动、尺寸或相关背景事件触发更新，平时保留画面 |

## 动态透景

`LiveGlass=true` 时，`LiquidGlassSurface` 清除旧位图、取消未完成的静态采样，并停止其节流计时器。此模式没有应用级背景截图循环，也不根据窗口是否活动、指针是否悬停来复制动画壁纸。切换回动态后，先前的静态任务不能再把旧图写回表面。

管理窗口始终保留 `AllowsTransparency=false`。`WindowBackdrop.Attach` 在 HWND 创建前附加 `WindowChrome`，客户区交给 DWM。`LiveGlassStyle` 在动态模式下提供两个风格：

| 值 | 界面名称 | 原生状态 |
| --- | --- | --- |
| `Clear` | 清透，默认 | `DwmExtendFrameIntoClientArea(-1)`、透明合成目标和 `DWMWA_SYSTEMBACKDROP_TYPE=1`，不附加 Acrylic |
| `Frosted` | 磨砂 | 扩展客户区并使用 `DWMSBT_TRANSIENTWINDOW=3`，即 Desktop Acrylic |

清透模式以透明客户区合成实际后方画面，再由 WPF 绘制半透明底色和边缘。`BackdropOpacity` 对应“窗口浓度”，范围为 .25–.9，默认 .65，控制管理窗口底色 alpha；Dock 透明度独立。清透由 `BackdropStatus.ClearActive` 标识，磨砂由 `AcrylicActive` 标识，两者分开记录。`SourceKind` 对可用的原生路径记录 `Native compositor`，不把清透误报为 Acrylic，也不把没有应用采样写成零延迟。

可调整大小的窗口还需对自身设置 `DWMWA_NCRENDERING_POLICY=DWMNCRP_DISABLED`，消除扩展客户区下面的实色标题栏填充；保留 `WindowChrome` 与原生调整尺寸样式。策略在窗口初始设置完成后及相关变化时重设，不运行帧计时器。磨砂和不透明回退恢复按窗口样式绘制。测试读取 `DWMWA_NCRENDERING_ENABLED`（属性 1）确认实际状态；策略属性 2 仅用于设置，不作为读回接口。

当前原生路径限定 Windows 11 build 22621 及以上，并检查系统透明效果、高对比度、会话与节电等支持条件。清透组合已在当前 Windows 11 验证环境取得实际透景，仍需覆盖不同驱动与硬件；Windows 10 未完成实机验证，不承诺透明效果。条件不满足或原生操作失败时使用不透明回退，用户也可手动选择磨砂。应用不替用户更改 Windows 透明效果设置，也不会用保留的静态截图代替动态背景。

Dock 使用 `AllowsTransparency=true`，透明活动区为放大图标留出空间。动态材质通过底座的真实 alpha 透出后方画面，不叠加一张延迟更新的背景图。关闭液态玻璃、启用应用内省电或高对比度时使用简单底色。Dock 不附加原生 Acrylic，不使用管理窗口的窗口浓度选项。

动态模式保留轮廓、颜色嵌线和边缘反光，不执行实时几何像素折射。移除应用截图层可以消除这层低频图像造成的停顿，但系统合成、显示器和壁纸程序仍有各自的调度；没有固定帧率或零延迟保证。

## 静态折射

关闭“动态透景”后，`LiquidGlassSurface` 才创建静态壁纸读取与区域采样资源。窗口事件经 80 ms 合并后，在后台处理有限区域的曲边取样位移与内部柔化。输出表面最多约 36 万像素，同尺寸复用一个 `WriteableBitmap`，相同场景可跳过重复光学计算。

静态模式不持续跟随动画。设置中的“静态折射清晰度”仅在此模式出现；低值更柔和，高值保留更多后方细节。它不改变系统 Acrylic 的原生模糊参数。

区域采样需要 Windows 10 build 19041 及以上、可用的桌面合成和自身窗口捕获排除能力。采样只临时改变本进程窗口的捕获属性，结束后恢复，不隐藏窗口，也不改变其他进程的属性。受保护内容不保证可取得。采样失败时尝试系统提供的可读静态壁纸，再回退到简单背景。

背景像素只在本地内存中处理，普通运行不保存背景截图、不写入日志或配置，也不上传。静态采样的短暂捕获排除可能让同时运行的截图或录屏程序漏掉 LumaDock；默认动态模式不使用这条采样路径。开发诊断模式可能单独保存程序渲染图，分享前需要检查内容。

## 轮廓、流光与图标

`DockPlate` 使用胶囊、卵石或轨道轮廓，绘制细边、内缘、窄阴影和强调色嵌线。`GlassRim` 接收同一底座轮廓；展平与弧长表只在布局或形态变化时更新，卵石的交错圆角也沿实际路径。

短反光由真实指针变化触发，最后一次输入后约 620 ms 收束，离开后约 180 ms 消退。相同坐标不续期；隐藏、卸载、减少动画或停止动效时清理计时器。它只改变二维反光，不驱动背景采样，也不执行流体模拟。

Dock 图标使用另一套有限插值，目标间隔为 16 ms。指针更新缩放、抬起和邻近位移目标，收敛后停止；离开后回落，隐藏、重建与关闭时清理。背景模式不决定这组动画的更新频率，计时器间隔也不等于帧率验收。

## 检查

```powershell
.\scripts\test-backdrop.ps1
.\scripts\test-sampler.ps1
.\scripts\test-lens.ps1
.\scripts\test-typography-ui.ps1
.\scripts\test-dock-ui.ps1
.\scripts\test-compositor-ui.ps1
```

检查覆盖原生材质与回退、已知背景下的区域采样、光学像素、Dock 轮廓与命中、有限动效及动态模式不保留快照等契约。实际执行结果和范围见 [验证记录](validation.md)。这些检查不代替不同壁纸程序、硬件、DPI、全屏游戏与长期驻留的验收。

## 接口资料

- [DWMWINDOWATTRIBUTE：非客户区状态、策略及背景属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
- [DWMNCRENDERINGPOLICY](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmncrenderingpolicy)

- [DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [Acrylic material](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [DwmExtendFrameIntoClientArea](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)
- [WindowChrome.GlassFrameThickness](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome.glassframethickness?view=windowsdesktop-10.0)
