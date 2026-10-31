# 名称字体

[返回使用指南](USER-GUIDE.md#6-在软件里调节名称字体) · [开发文档](DEVELOPMENT.md)

LumaDock 提供两种名称显示方式：跟随 Windows 当前图标标题字体，或保存一套独立的名称字体。调整范围是应用库、桌面集中的应用名称和 Dock 名称提示；标题、搜索框与设置说明继续使用界面字体。

## 独立调整

1. 打开“偏好设置”，点击“文字”。
2. 关闭“跟随 Windows 图标字体”。
3. 在已安装字体中选择，调整名称字号、加粗与斜体。
4. 查看“名称预览”。松开滑块或结束键盘操作后，设置自动保存。

“界面默认”使用系统字体回退顺序。字体列表不下载、安装或加载额外字体文件。导入配置中指定的字体若未安装，会由 WPF 回退显示，原家族名称仍保留以便辨认。

名称字号范围是 **7.5–30 磅**。内部使用 10–40 WPF DIP，换算关系为 `1 磅 = 4/3 DIP`。大字号会降低网格列数，增加两行名称的空间，每页显示的应用数量也会随之减少。

独立字体只改变 LumaDock 的配置，不更改 Windows 全局字体。重新开启系统跟随后，手动选择仍保留，关闭跟随可继续使用。

## 跟随 Windows

“跟随 Windows 图标字体”默认开启，读取当前已经生效的图标标题字体，包括家族、字号、粗细、斜体、下划线和删除线；Dock 名称提示同步家族、字号、粗细和斜体。

Windows 设置实际生效后，切回或重新打开 LumaDock 会重新读取。需要手动更新时，点击“名称字体 → 系统图标字体 → 刷新字体”。尚未应用或需要注销才能生效的系统更改不会提前显示。

## 实现与配置

`DesktopFontService` 优先使用 `SystemParametersInfoForDpi(SPI_GETICONTITLELOGFONT, dpi=96)`，保存为 WPF DIP，由窗口所属显示器执行缩放。旧系统缺少该入口时，回退至普通 `SystemParametersInfoW` 并按系统 DPI 换算；读取失败再使用 WPF 系统字体。

负 `lfHeight` 表示字符高度；正值表示包含内部行距的字符单元。后者通过临时 GDI 字体测量去掉内部行距，DC 和字体句柄在读取结束后释放。

服务在创建时读取一次，合并系统偏好通知并投递到 UI Dispatcher。`WM_SETTINGCHANGE`、`WM_FONTCHANGE` 和窗口重新显示也会触发刷新。数值未改变时不发出更新、不保存配置，也不因此重建 Dock；释放服务时解除静态事件订阅。

相关设置：`FollowSystemIconFont`、`NameFontFamily`、`NameFontSize`、`NameFontBold`、`NameFontItalic`。导入只接受有限长度的家族名称，URI、字体文件路径与控制字符会归一为默认值。

## 验证

```powershell
.\scripts\test-font.ps1
.\scripts\test-typography-ui.ps1
```

只读字体检查比较原生读取与 WPF 系统快照，验证无变化刷新及资源释放。实际 WPF 检查覆盖名称控件、字号、家族、斜体、预览和布局。测试使用隔离的应用配置，不改写 Windows 字体；通过情况与覆盖范围见 [验证记录](validation.md)。

## 接口资料

- [SystemParametersInfoForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfofordpi)
- [SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [LOGFONTW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfontw)
- [UserPreferenceCategory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.userpreferencecategory?view=windowsdesktop-10.0)
