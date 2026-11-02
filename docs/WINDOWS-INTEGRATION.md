# Windows 集成

[返回架构](architecture.md) · [开发与测试](DEVELOPMENT.md) · [使用指南](USER-GUIDE.md)

## 应用发现与桌面集

后台 STA 读取当前用户与公共开始菜单、桌面上的 `.lnk`、`.url` 和程序入口，并通过 `Shell.Application` 的 `shell:AppsFolder` 获取 Windows 注册应用。扫描不遍历整块磁盘；未注册的便携软件由用户手动添加。

Shell 名称、AUMID、快捷方式目标、参数和工作目录共同参与身份整理。等价入口合并时保留用户的固定顺序、图标、隐藏记录和桌面关联。同名且指向同一本地程序的有效启动变体由展示层收在一张卡片，原始启动记录仍各自保留。

应用库展示全部有效记录。桌面集根据 `DesktopPaths` 中实际存在的文件或有效的 Desktop 来源入口筛选；每次重建视图时拍下关联快照，搜索和布局过程中不反复检查文件。两页共用整理数据，均可使用自建栏目。

图标由后台 STA 中的 `IShellItemImageFactory` 提取，回退至 `SHGetFileInfo`，返回冻结 WPF 位图并使用有界缓存。HBITMAP、拥有的 HICON 和 COM 引用按生命周期释放。窗口图标的句柄来自目标应用，复制为冻结像素后不销毁借用句柄；识别不了的窗口可以使用 Windows 默认应用图标。

## 真实窗口与临时任务

`SetWinEventHook` 监听前台切换、最小化、创建、销毁和显隐。合并事件后更新真实顶层窗口，不以固定频率扫描进程；位置事件只影响相关前台窗口的全屏判断。

筛选排除工具窗口、不激活辅助窗口、DWM cloaked 窗口、系统 Shell 表面，以及由其所有者负责的普通模态对话框。身份优先采用完整 AUMID，明确冲突的 AUMID 不回退到同一宿主 exe。带参数的浏览器、脚本运行时等共用宿主不会仅凭 exe 被固定项认领；无法可靠识别的任务仍以真实窗口处理。

未固定窗口按可验证的 AUMID、exe 或窄化进程身份形成临时组。分组只供 Dock 展示，不自动写入应用库、固定列表或用户栏目；窗口关闭后移除。用户主动固定时，只有可以安全重建启动入口的应用才提供该操作。

激活前检查 HWND 仍存在且 PID 未改变，还原最小化窗口，再请求 `SetForegroundWindow`。同应用多窗口可循环切换；菜单按需读取当前标题，避免后台不断刷新标题。最小化使用普通窗口请求，关闭只发送 `WM_CLOSE`，由目标应用决定保存或拒绝，绝不终止进程。

## 系统区与原生入口

| 部分 | 实现与边界 |
| --- | --- |
| 后台应用 | 通过 UI Automation 查找 Explorer 的真实溢出入口并调用 Invoke，确认原生弹窗可见后才报告成功。入口不可用或系统拒绝时提示失败 |
| 中／英模式 | 记住先前前台窗口的 HWND、PID 与线程，读取其 IMM 兼容状态；必要时读取系统输入指示器提供的实际状态。不可读取的模式显示不可用 |
| 键盘布局 | TSF 枚举实际启用的文本服务和布局，保留独立 profile 身份；选择时请求激活并遵守前台和权限限制，不注册或启用新输入法，不改默认语言 |
| 网络 | 读取有效网络接口，由网络变更事件更新；连接状态不代表已经通过互联网探测 |
| 电源 | 读取 Windows 电源状态并监听系统电源事件；无电池和未知百分比分开呈现 |
| 音量与静音 | CoreAudio 默认播放端点，监听端点与设备变化。设备更换时重新绑定；失败后按实际状态刷新 |
| 亮度 | 按需使用 `root/WMI` 中活动的 `WmiMonitorBrightness` 和 `WmiMonitorBrightnessMethods.WmiSetBrightness`；目前限支持该接口的内置屏幕 |
| 时间与月历 | 使用本地日期时间，按下一分钟边界更新；Dock 隐藏时停止定时器，月历按打开和翻月绘制 |

后台托盘保留 Windows 的原生弹窗和应用菜单，不复制第三方托盘图标或接管其消息。自动隐藏任务栏时仍依赖正常运行的 Explorer；被其他工具完全移除的任务栏或组织策略可能让入口不可用。没有 Explorer 注入、跨进程托盘内存读取，也没有 Shell 替换。

快捷控制、键盘布局与月历使用按需创建的原生 WPF 窗口，接入与主窗口相同的玻璃背景支持。关闭时释放窗口事件、滑块节流与本地动画。用户更改音量和亮度后才提交系统操作，读取值或刷新界面不会触发写入。

WLAN、蓝牙和完整系统快捷设置打开真实 Windows 入口；开始、搜索、任务视图、显示桌面和通知使用固定、平衡的系统快捷键序列。发送前检查物理修饰键，部分提交时只释放已经注入且仍按下的按键；系统拒绝或权限不匹配会返回失败。Windows 10 与 Windows 11 的通知入口按版本分开处理。

相关公开接口：[TSF profile 管理](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfinputprocessorprofilemgr)、[CoreAudio 音量变更回调](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback)、[WMI 屏幕亮度接口](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbrightnessmethods)。

## 窗口层级、全屏与显示器

Dock 使用普通窗口层级、工具窗口与 `WS_EX_NOACTIVATE`。打开其他软件时保留应用在 Dock 上方；重定位、刷新和重建不会把 Dock 设为常驻置顶。Dock 不占任务栏条目，不注册 AppBar，也不改变系统工作区。

`EnumDisplayMonitors` 与 `GetMonitorInfo` 提供物理像素边界，允许负坐标；应用声明 PerMonitorV2，按 DPI 换算 DIP。显示器断开时回退主屏。弹窗限制在所选显示器工作区内，并按四种 Dock 方位定位。

全屏判断比较前台窗口与所选显示器完整边界；只有目标屏幕被覆盖时才抑制 Dock。独占游戏、提权程序、混合 DPI 双屏和远程会话需要各自实际验收。

## 用户主动选择的系统操作

- **桌面图标显示**：确认后查找系统桌面的 `SysListView32` 并请求显示或隐藏；查找失败时返回失败，不修改文件。Windows 桌面右键“查看 → 显示桌面图标”可恢复。
- **开机启动**：确认后创建或移除当前用户启动文件夹内的 LumaDock 自有快捷方式，不写 Run 注册表，不创建服务或计划任务。
- **任务栏自动隐藏**：由用户在 Windows 任务栏设置中选择，LumaDock 不自行切换。
- **应用快捷键**：注册 Ctrl + Alt + Space，冲突时可使用 Dock 或托盘；退出时注销事件和热键。

## 验证方式

```powershell
.\scripts\test-windows.ps1
dotnet run --project tests/LumaDock.System.Tests -c Release
dotnet run --project tests/LumaDock.System.Ui.Tests -c Release
```

Windows 集成测试读取真实 Shell、图标、显示器和窗口，快捷方式写入使用隔离目录，窗口动作只针对测试自己创建的窗口。系统测试读取真实设备状态，按键发送使用注入的替身检查序列；不会自动切换用户输入法、调整音量或更改亮度。界面检查验证真实 WPF 控件、事件生命周期与布局。

这些检查不能代替全部 Windows 版本、第三方输入法、显卡、混合 DPI 显示器或长期驻留验收。已取得的结果与未覆盖场景见 [验证范围](validation.md)，性能数字见 [性能记录](performance.md)。
