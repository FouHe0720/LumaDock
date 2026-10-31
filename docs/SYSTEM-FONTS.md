# 名称字体

0.5 可在 LumaDock 内直接调节应用名称的字体，也保留默认的 Windows 字体跟随模式。调整范围为应用库、桌面集的名称及 Dock 提示；标题、搜索框与设置说明继续使用界面字体。

## 在软件内调整

1. 打开“偏好设置”，点击顶部“文字”，或滚动到“名称字体”。
2. 关闭“跟随 Windows 图标字体”。
3. 选择“字体”，调节“名称字号”“加粗”“斜体”。下方“名称预览”使用本机实际应用名称实时显示。

字体列表来自已安装的字体，可输入名称首字母定位。“界面默认”使用 `Segoe UI Variable Text, Microsoft YaHei UI` 回退顺序，不下载或安装字体。未安装的导入字体会由 WPF 回退处理，原名称仍保留在设置中供辨认。

界面用磅显示字号，保存为 WPF DIP：`1 磅 = 4/3 DIP`。手动范围为 7.5–30 磅，即 10–40 DIP。滑动时预览立即改变，松开滑块或结束键盘操作后保存；应用网格随字号调整列数和两行名称空间。字号越大，每页容纳的应用越少。

这些控件只修改 LumaDock 的配置，不修改 Windows 全局字体、注册表或其他软件。没有嵌入 changesize 的可执行文件。开启“跟随 Windows 图标字体”后使用系统值，原手动选择保留，关闭跟随后重新使用。

相关配置：`FollowSystemIconFont`、`NameFontFamily`、`NameFontSize`、`NameFontBold`、`NameFontItalic`。字体名只接受有限长度的家族名称；导入中的 URI、字体文件路径和控制字符会归一为默认值，不按字体来源加载外部文件。

## 配合 changesize

“跟随 Windows 图标字体”默认开启。应用名称读取 Windows 的桌面图标标题字体，包括字体、字号、粗细、斜体、下划线与删除线。System Font Size Changer（changesize）中的 **Icon** 对应这一设置；软件只读取已经应用到 Windows 的值。

在 changesize 中调好 **Icon** 后点击 **Apply**，再切回或重新打开 LumaDock。仍未更新时，使用“偏好设置 → 名称字体 → 系统图标字体 → 刷新字体”。尚未应用或需要注销才能生效的数值不能视作当前 Windows 字体；LumaDock 不会代替外部工具完成系统修改。

## 原生读取与通知

`DesktopFontService` 优先使用 `SystemParametersInfoForDpi(SPI_GETICONTITLELOGFONT, dpi=96)`，将结果保留为 WPF DIP。WPF 随窗口所在显示器执行 DPI 缩放，避免字体被重复放大。Windows 10 1607 以前没有此入口时，回退到普通 `SystemParametersInfoW`，再按系统 DPI 换算。两者均失败时使用 WPF 的系统图标字体。

负 `lfHeight` 是字符高度；正值是包含内部行距的字符单元高度，服务会通过临时 GDI 字体测量去掉内部行距。临时 DC 和字体句柄均在本次读取后释放。

服务在创建时读取一次，监听相关的系统偏好通知，将短时间内的通知合并投递到 UI Dispatcher。没有定时查询。窗口收到 `WM_SETTINGCHANGE` 或重新打开时也可调用 `Refresh()`；数值未变化不会发出 `Changed`，不会因此重建界面或写配置。`Dispose()` 移除静态事件订阅。

## 只读验证

```powershell
.\scripts\test-font.ps1
```

测试读取实际系统字体，与 WPF `SystemFonts` 交叉核对，验证无变化刷新不触发通知、释放后不刷新、静态事件不保留已释放服务。不开窗口、不修改系统字体、不模拟外部工具点击。记录为 `artifacts/tests/font-results.json`。

0.4 的 2026-10-31 本机原生读取结果为 Microsoft YaHei UI、15 DIP、字重 700（粗体），与当时 `IconFont` 设置一致。此数值是本机状态，不是应用写死的默认值。0.5 本轮实际 WPF 测试 14/14 通过，包含手动字体家族、斜体及配置保存；预览操作、其余视觉和验收边界见 [验证记录](validation.md)。没有实际操作 changesize 的 Apply 更改系统字体来完成端到端验收，也不在本轮加入全局字体写入。

## 微软资料

- [SystemParametersInfoForDpi：指定 DPI 的图标字体读取及支持版本](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfofordpi)
- [SystemParametersInfoW：SPI_GETICONTITLELOGFONT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [LOGFONTW：高度、字重和样式的含义](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfontw)
- [UserPreferenceCategory：系统偏好通知分类](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.userpreferencecategory?view=windowsdesktop-10.0)
