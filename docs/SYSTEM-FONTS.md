# 字体与文字

[返回使用指南](USER-GUIDE.md#6-字体与文字) · [开发文档](DEVELOPMENT.md)

LumaDock 可以跟随 Windows 当前图标标题字体，也可以保存独立的字体设置。字体、粗细与斜体同时用于侧栏、栏目、标题、搜索和设置文字，以及应用名称与 Dock 名称提示。

应用名称的字号随所选字体设置变化；标题、导航和说明保留各自的字号，避免大字号挤满侧栏。标题与选中标签至少使用半粗，系统字体更粗时会继续跟随。界面符号不随文字加粗或倾斜。

## 独立调整

1. 打开“偏好设置”，点击顶部“文字”，定位到“字体与文字”。
2. 关闭“跟随 Windows 图标字体”。
3. 在已安装字体中选择，调整名称字号、加粗与斜体。
4. 查看“文字预览”。松开滑块或结束键盘操作后，设置自动保存。

“界面默认”使用系统字体回退顺序。字体列表不下载、安装或加载额外字体文件。导入配置中指定的字体若未安装，会由 WPF 回退显示，原家族名称仍保留以便辨认。

名称字号范围是 **7.5–30 磅**。内部使用 10–40 WPF DIP，换算关系为 `1 磅 = 4/3 DIP`。大字号会降低网格列数，增加两行名称的空间，每页显示的应用数量也会随之减少。

独立字体只改变 LumaDock 的配置，不更改 Windows 全局字体。重新开启系统跟随后，手动选择仍保留，关闭跟随可继续使用。

## 文字颜色

“文字颜色”提供自动、白瓷、冰蓝、薄荷、砂金和墨色。颜色用于侧栏名称、栏目名称、页面标题、应用名称、Dock 名称提示等主要文字；说明、计数和强调色按钮继续保留各自的配色。

点击“自选颜色…”可输入六位十六进制色值，例如 `#D7ECF4`，留空恢复自动。颜色即时显示并保存；自选颜色在深浅主题间保持不变，切换背景后若不易辨认，可以改回“自动”。自动配色随主题切换，高对比度模式使用 Windows 窗口文字颜色。

文字颜色与系统字体跟随独立，可以在跟随 Windows 字体时单独选色。颜色仅影响 LumaDock，不更改 Windows 或其他应用。

## 跟随 Windows

“跟随 Windows 图标字体”默认开启，读取当前已经生效的图标标题字体，包括家族、字号、粗细、斜体、下划线和删除线；Dock 名称提示同步家族、字号、粗细和斜体。

LumaDock 只读取已经生效的 Windows 字体设置。系统设置实际生效后，切回或重新打开 LumaDock 会重新读取。需要手动更新时，点击“偏好设置 → 字体与文字 → 系统图标字体 → 刷新字体”。尚未应用或需要注销才能生效的系统更改不会提前显示。

## 实现与配置

`DesktopFontService` 优先使用 `SystemParametersInfoForDpi(SPI_GETICONTITLELOGFONT, dpi=96)`，保存为 WPF DIP，由窗口所属显示器执行缩放。旧系统缺少该入口时，回退至普通 `SystemParametersInfoW` 并按系统 DPI 换算；读取失败再使用 WPF 系统字体。

负 `lfHeight` 表示字符高度；正值表示包含内部行距的字符单元。后者通过临时 GDI 字体测量去掉内部行距，DC 和字体句柄在读取结束后释放。

服务在创建时读取一次，合并系统偏好通知并投递到 UI Dispatcher。`WM_SETTINGCHANGE`、`WM_FONTCHANGE` 和窗口重新显示也会触发刷新。数值未改变时不发出更新、不保存配置，也不因此重建 Dock；释放服务时解除静态事件订阅。

`Typography` 通过动态资源分发界面字体、字重、斜体和主要文字颜色，系统字体变化时已有控件会更新，无需常驻轮询。名称保留完整的图标字体字号及装饰线，其他界面文字使用独立的字号层级。

相关设置：`FollowSystemIconFont`、`NameFontFamily`、`NameFontSize`、`NameFontBold`、`NameFontItalic`、`TextColor`。导入只接受有限长度的家族名称，URI、字体文件路径与控制字符会归一为默认值。`TextColor` 仅接受 `#RRGGBB` 并归一为大写，空值或无效值使用自动配色；不从字符串加载资源或外部文件。

## 验证

```powershell
.\scripts\test-font.ps1
.\scripts\test-typography-ui.ps1
```

只读字体检查比较原生读取与 WPF 系统快照，验证无变化刷新及资源释放。实际 WPF 检查覆盖侧栏、栏目、标题和名称控件的字重与家族、名称布局、选色与保存、主题切换和自动配色恢复。字体变化通知使用本地服务注入验证资源更新，不代表已操作外部字体工具。测试使用隔离配置，不改写 Windows 字体；通过情况与覆盖范围见 [验证记录](validation.md)。

## 接口资料

- [SystemParametersInfoForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfofordpi)
- [SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [LOGFONTW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfontw)
- [UserPreferenceCategory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.userpreferencecategory?view=windowsdesktop-10.0)
