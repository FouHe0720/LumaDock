# LumaDock

原生 WPF Windows 桌面 Dock 与应用管理器。C# / .NET 10 LTS / Win32 / Windows Shell，无 Electron、Chromium、WebView2，也无后台服务。

当前版本为 **0.5.0 预发布版本**。本轮增加短暂的玻璃流光、四种强调色和内置名称字体调节，导航改为“应用库、桌面集、偏好设置、关于”。变化见 [0.5 发布说明](docs/RELEASE-0.5.md)。Release 构建、组件及 WPF 回归已通过，并完成本机深色界面、字体预览检查和两轮短时资源采样；完整范围见 [验证记录](docs/validation.md) 和 [性能记录](docs/performance.md)。目标系统为 Windows 10/11 x64，Windows 10、真实混合 DPI 双屏和长期驻留仍需专项实测。

## 快速运行（无需 IDE）

发布包名为 `artifacts/LumaDock-0.5.0-win-x64.zip`。解压**整个压缩包**到用户可写目录，双击 `LumaDock.exe`。不要仅复制 exe；同目录包含自带的 .NET Desktop Runtime。发行包不需要安装 SDK，不注册服务，不需要管理员权限。

首次启动会扫描当前用户及公共开始菜单、桌面快捷方式，以及 Windows `shell:AppsFolder` 中可枚举的应用。后台扫描完成后一次性更新应用列表。Dock 默认固定最先发现的少量真实应用，可自行调整。点击 Dock 最左侧启动台图标，或按 **Ctrl+Alt+Space** 打开管理窗口；快捷键被其他软件占用时使用系统托盘菜单。

可为 `LumaDock.exe` 创建桌面快捷方式；重复双击会显示已运行的窗口。源码构建后，也可在项目根目录执行下面的脚本。脚本默认指向项目的 `artifacts\publish\LumaDock.exe`，例如 `D:\LumaDock\artifacts\publish\LumaDock.exe`；移动发行目录后需重新创建快捷方式。

```powershell
.\scripts\create-shortcut.ps1 -Destination ([IO.Path]::Combine([Environment]::GetFolderPath('DesktopDirectory'), 'LumaDock.lnk'))
```

## 开发依赖与命令构建

开发需要 Windows x64 和 .NET **10.0.403 SDK**（`global.json` 允许同补丁系列更新）。可从 [微软 .NET 10 下载页](https://dotnet.microsoft.com/download/dotnet/10.0) 获取 Windows x64 SDK，安装到系统，或将微软官方 SDK zip 解压到项目 `.dotnet`。本机使用 `D:\LumaDock\.dotnet` 中的项目本地 SDK，不修改系统 PATH 或注册表；只使用便携发布包时无需 SDK。

在 PowerShell 中执行：

```powershell
Set-Location D:\LumaDock
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\start.ps1
```

`build.ps1` 默认 Release；可加 `-Configuration Debug`。脚本优先使用项目 `.dotnet\dotnet.exe`，否则使用 PATH 中的 dotnet。若系统执行策略阻止脚本，请先查看脚本内容，可对本次 PowerShell 进程临时使用 `-ExecutionPolicy Bypass`，无需永久改变机器策略。

不使用辅助脚本时：

```powershell
.\.dotnet\dotnet.exe build LumaDock.slnx -c Release
.\.dotnet\dotnet.exe run --project tests\LumaDock.Tests -c Release
.\.dotnet\dotnet.exe .\src\LumaDock.App\bin\Release\net10.0-windows\LumaDock.dll
```

不要把 Linux/WSL 编译通过当作 Windows WPF 验证。正式运行、Shell 集成及 UI 测试均须在 Windows 完成。

## 添加、搜索和整理应用

**应用库**汇集开始菜单、桌面、商店及手动添加的应用；**桌面集**只显示来自桌面的应用快捷方式。同一个软件可以同时出现在两页。两页共享分类和固定状态，不移动真实文件。

- 启动台输入应用名称或路径的部分内容进行搜索，按完整输入字符串匹配子串；分类通过分组标签筛选。
- 点击应用打开它；有匹配的运行窗口时优先切换到该窗口。不能读取目标进程或无法准确匹配的情况不显示伪造运行状态。
- 点击“添加应用”选择 `.exe`、`.lnk`、`.url` 或 `.appref-ms`，也可把支持的文件拖入 Dock。
- 右键应用可固定/取消固定到 Dock、移入分组或隐藏管理记录。隐藏仅更改 LumaDock 的配置，**不会卸载应用、删除或移动原文件**。设置中心可恢复隐藏记录。
- “新建分组”创建虚拟分类；把应用拖到分类标签进行归类，拖到另一个应用前面调整顺序。分组是本地管理记录，不改变安装位置。
- 安装新软件后，使用应用页的“刷新”或托盘“重新扫描应用”。扫描保留用户分类、排列与隐藏记录。

系统目录、受限 Shell 条目、损坏快捷方式可能无法解析。原始快捷方式仍由 Windows Shell 启动；若自动扫描未包含某个应用，可以手动选择路径。应用图标来自实际 Shell/程序资源；图标无法读取时保留空位而不捏造品牌图标。

## Dock 使用

点击图标启动或切换窗口。悬停显示应用名，图标及附近图标产生适度放大；运行指示点来自可识别的真实顶层窗口。拖动已固定图标排序，右键移除；排列自动保存。

Dock 右键菜单提供启动台、添加应用、设置与退出。系统托盘菜单还提供重新扫描与重启 Dock。自动隐藏开启后，鼠标离开会短暂延迟收起，移到设置屏幕的对应工作区边缘可重新显示。Dock 使用显示器工作区定位，避开正常任务栏区域；检测到前台全屏窗口时隐藏。特殊游戏、独占全屏和 Shell 替代环境仍需实机测试。

## 外观、性能与双屏

“偏好设置”可调主题、Dock 透明度、圆角、图标尺寸、间距、位置、自动隐藏、启动台列数和关闭行为。点击顶部“材质”可跳到“材质与颜色”。玻璃保留 0.4 的背景折射：取样窗口或 Dock 后方的真实画面，让边缘附近的图像产生位移和弯曲，内部柔化。可以透出桌面、Wallpaper Engine 已绘制的动态壁纸和后方应用画面；无法取得画面时，按可用条件回退到静态壁纸、Windows 原生磨砂或不透明背景。

“偏好设置 → 材质与颜色”提供：

- **强调色**：潮蓝、青瓷、琥珀、烟紫，用于按钮、选中状态和玻璃边缘；深浅色分别配色。
- **液态玻璃**：默认开启。关闭后使用系统磨砂；系统不支持时使用不透明背景。
- **动态透景**：默认开启。管理窗口正在使用时更新，Dock 仅在鼠标停留时更新；窗口不活跃或 Dock 空闲时停止持续更新。关闭后仍可保留玻璃，在打开、移动、调整大小等事件时重绘。
- **玻璃流动**：默认开启。指针移动时，边缘反光沿圆角短暂流动，停下后收住；它与后方画面的更新是两种效果，可分别关闭。
- **流动幅度**：默认 45%，范围 0%–100%；设为 0% 时停止流光。隐藏、不活跃或减少动态效果时停止动画，空闲不持续播放。
- **玻璃清晰度**：默认 65%，范围 10%–100%。较低数值让背景更柔和，较高数值保留更多细节。

设置会自动保存；字号和幅度滑块在松开或结束键盘操作时保存。玻璃流光是短暂的二维反光，不是持续运行的流体模拟。

动态透景会增加采样和 CPU 渲染开销。它按有限频率更新，不是 60 fps 视频透传；希望降低使用中的占用时，可先关闭“动态透景”，或启用“省电模式”关闭折射处理。省电、关闭动画等减少动态效果的条件下，不启动持续透景更新。

取样只覆盖窗口或 Dock 及其边缘附近，像素在本机内存中处理，普通运行不保存背景截图到配置或日志，也不上传。采样期间临时将 LumaDock 自身窗口排除，结束后恢复原状态；不会改动其他程序窗口。Windows 10 2004 以前不启用这条动态取样路径，优先使用可读取的系统静态壁纸，否则回退到系统背景。

此前按用户提供的 `D:\GlassDesk` 只读研究了边缘与反馈。当前折射、柔化与细边缘为 LumaDock 自行实现，没有复制其源码、着色器或素材，本次也没有新增 SDK 或第三方组件。Windows 11 22621 及以上保留 DWM 原生 Desktop Acrylic 作为基础与回退；LumaDock 不更改 Windows 的透明效果设置。

连接双显示器后，在“Dock 显示器”选具体设备；留空/主显示器为默认。显示器断开后回退主屏。WPF 使用 PerMonitorV2 DPI 感知；移动显示器、缩放比例变化后重新定位。请分别在 100%/125%/150%/200% 缩放验证；本机未连接的物理屏幕组合不声称已验收。

省电模式、动画关闭或电池供电时减少动画。Dock 空闲不使用持续帧渲染循环；窗口运行状态由 Windows 事件触发更新，图标异步缓存。应用扫描按启动/手动请求执行，未进行持续后台磁盘扫描。

## 直接调节名称字体

打开“偏好设置”，点击顶部“文字”跳到“名称字体”。关闭“跟随 Windows 图标字体”后，可以直接选择本机字体、调节名称字号、加粗和斜体；下方“名称预览”实时使用实际应用名称显示效果。这些设置作用于应用库、桌面集的应用名称和 Dock 名称提示，**只更改 LumaDock，不更改 Windows 全局字体**。

“字体”列表来自本机已安装字体，可输入名称首字母查找；“界面默认”使用内置字体回退顺序，不安装或下载字体。名称字号范围为 7.5–30 磅（10–40 DIP），大字号自动减少网格列数、为两行名称留出空间。标题、搜索框和设置文字使用各自的界面字体。

“跟随 Windows 图标字体”默认开启。开启时，应用名称读取 Windows 当前的图标标题字体，包括字体、字号、粗细、斜体、下划线和删除线；Dock 的 WPF 提示同步字体、字号、粗细和斜体。手动设置仍保留，关闭跟随后可继续使用。

若继续使用 System Font Size Changer（changesize），在工具中修改 **Icon** 并点击 **Apply**，再切回或重新打开 LumaDock；仍未更新时，在“名称字体 → 系统图标字体”点击“刷新字体”。外部工具要求注销时，需先让 Windows 设置生效。读取方式、范围及测试边界见 [名称字体说明](docs/SYSTEM-FONTS.md)。本轮不提供全局字体写入，也不包含 changesize 程序本身。

## 桌面整理与恢复 Windows 桌面

“桌面集”显示扫描到的桌面应用快捷方式，可添加到应用库/Dock，或归入虚拟分组。该模块不移动桌面文件。当前未提供桌面时钟。

桌面图标显示按钮会先提示确认，确认后仅改变 Explorer 桌面图标窗口显示。可在同一位置重新显示，或在 **Windows 桌面右键 → 查看 → 显示桌面图标** 恢复。如果 Explorer 重启后状态改变，使用 Windows 自带菜单重新设置。LumaDock 不替换 Explorer、不注入进程、不修改系统文件，退出时也不会强制改回用户主动选择的桌面状态。

## 开机启动、退出与卸载

开机启动默认关闭。在设置中启用会单独确认，只在当前用户的“启动”文件夹创建 `LumaDock.lnk`，不写 Run 注册表。请先把发行目录放在长期保留的位置，再从 `LumaDock.exe` 开启；之后移动程序需先关闭再重新开启。Windows 自带启动管理可能额外禁用该项，最终状态以 Windows 设置为准。

关闭管理窗口默认驻留托盘。正常结束请使用 Dock/托盘“退出 LumaDock”；关闭“关闭窗口后驻留托盘”后，关闭管理窗口也会退出。

卸载步骤：先关闭开机启动并确认，再正常退出；恢复需要的桌面图标显示；随后删除 LumaDock 发行目录即可。程序用户数据位于 `%LOCALAPPDATA%\LumaDock`，仅在不再需要配置/日志时手动删除该目录。也可按 `Win+R` 输入 `shell:startup`，仅移除本软件的 `LumaDock.lnk`。请不要删除其他启动项或桌面快捷方式。

## 数据与日志

默认数据目录：`%LOCALAPPDATA%\LumaDock`。设置中心提供配置 JSON 导入、导出和恢复默认值。导入会替换整理结果，建议先导出备份；导入和恢复默认值不会自动授权启用开机启动，也不会更改桌面图标显示。

`config.json` 使用版本号、临时写入后替换及 `.bak` 备份。损坏配置保留为 `.corrupt-时间戳`，优先恢复上次备份，不能恢复时使用默认配置。遇到更高版本配置会拒绝读取，避免旧版覆盖新配置。日志位于 `logs/lumadock.log`，达到大小上限后轮转；日志仅保存在本机。

诊断运行可指定隔离目录：

```powershell
& .\artifacts\publish\LumaDock.exe --data-dir D:\LumaDock\work\sandbox --diagnostics-dir D:\LumaDock\artifacts\diagnostics --show-launchpad
```

## 测试、性能测量与发布

`scripts/test.ps1` 执行无需第三方框架的组件测试，包含配置往返、原子备份、损坏恢复、未来版本保护、分类合并、搜索及字体/玻璃设置归一，0.5 本轮 **25/25 通过**。`scripts/test-windows.ps1` 只读验证 Shell 扫描、真实图标、显示器及窗口身份。JSON 结果在 `artifacts/tests/results.json` 和 `windows-results.json`；不同报告的日期和版本需分别查看。0.5 Release 构建为 0 警告、0 错误；本机已检查深色青瓷界面、名称预览与斜体保存，未改变 Windows 全局字体。

折射、采样和壁纸已有验证记录（0.4）：

| 命令 | 已取得的结果 | 证据 |
| --- | --- | --- |
| `scripts/test-lens.ps1` | 13/13；测试图像的边缘位移、内部柔化、透明圆角、缩放采样及取消/尺寸保护 | `artifacts/tests/lens-results.json` |
| `scripts/test-sampler.ps1` | 6/6；真实窗口遮挡下取得已知背景色、恢复自身采样排除状态、不改变其他进程窗口及 10 次采样的 GDI 资源检查 | `artifacts/tests/sampler-results.json` |
| `scripts/test-wallpaper.ps1` | 6/6；只读 Shell 壁纸和显示器信息、刷新/释放行为；本机未提供可读静态壁纸文件，按缺失来源处理 | `artifacts/tests/wallpaper-results.json` |

折射测试使用合成图像，采样测试使用独立进程的已知颜色背景；这些能验证算法和本机接口行为，不能代替最终桌面视觉或应用性能验收。测试文件内的单次算法计时也不是整款软件的空闲性能。

字体和布局验证：

| 命令 | 本轮结果 | 证据 |
| --- | --- | --- |
| `scripts/test-layout.ps1` | 7/7；大字行高、列数上限、小窗口及非法输入 | `artifacts/tests/layout-results.json` |
| `scripts/test-font.ps1` | 5/5；读取当前 Windows 图标字体、与 WPF 交叉核对及释放订阅 | `artifacts/tests/font-results.json` |
| `scripts/test-typography-ui.ps1` | 0.5 本轮 14/14；真实 WPF 字体、手动家族/斜体与保存、强调色、设置快捷跳转与预览控件，以及流光停止与静止指针回归 | `artifacts/tests/typography-ui-results.json` |

字体测试不改变 Windows 字体；28/40 DIP 是隔离 LumaDock 配置中的模拟设置。测试核对 WPF 控件和布局，不能代替最终观感检查，也没有验证 changesize 的 Apply 按钮及其字体变更广播。

`scripts/test-ui.ps1` 在 Windows 会话中创建真实 WPF 窗口，完成 **100 次显示/搜索/导航/隐藏**、3 次窗口重建和关闭动画期间重新显示的回归，已通过且无异常，记录在 `artifacts/tests/ui-results.json`。100 次循环关闭动画、禁用测试窗口输入以避免抢焦点；另有动画期间的非分层 HWND 检查。测试不启动其他应用或修改系统设置，不能代替鼠标拖拽、动画体验、全屏游戏和过夜验收。请与性能采样分开运行。

`scripts/test-activation.ps1` 的 **6 项测试**通过，包括从真正的第二个 Windows 进程唤起驻留实例、启动等待、隔离和超时。`scripts/test-backdrop.ps1` 的 **4 项测试**通过，核对原生背景属性、省电回退、材质恢复及最大化工作区；原生属性验证仍需结合实际截图检查视觉效果。结果同样位于 `artifacts/tests`。

```powershell
.\scripts\publish.ps1
.\scripts\measure-performance.ps1 -Seconds 120 -WarmupSeconds 15
.\scripts\measure-performance.ps1 -Seconds 120 -ShowLaunchpad
```

发布为 `win-x64` 自包含目录和 zip，不进行 WPF 裁剪。性能脚本启动独立数据目录的测试实例，采样完成仅结束该测试实例，保存 CSV 与 JSON 到 `artifacts/performance/时间戳`。请在关闭其他 LumaDock 测试实例、桌面空闲且鼠标离开 Dock 时运行；诊断扫描属于首次启动开销。详细指标口径见 `docs/performance.md`。

**0.5 最终发布二进制已完成两轮 60 秒短测。**Dock 空闲平均全机 CPU 0.00912%、私有工作集峰值 47.883 MB；启动台前台为 0.11618%、58.055 MB，首帧 289.4 ms。启动台单核 CPU 口径为 1.85888%，私有提交峰值 117.465 MB、总工作集 204.359 MB，与私有工作集不同。测试时指针静止、流光已收住，不能代表持续移动指针时的成本。完整哈希、焦点状态、原始 CSV 和历史版本见 [性能记录](docs/performance.md)。没有 GPU、所有交互状态或长期驻留的资源保证。

仓库提供 `.github/workflows/build.yml`，在 Windows GitHub Actions runner 上构建解决方案、运行 Core/Storage、像素折射与布局组件测试，并生成便携包和测试报告。它不运行真实桌面的视觉、Shell、游戏或性能验收；远程运行结果以 GitHub Actions 工作流为准。

性能目标是方向，不是承诺：空闲 CPU 接近 0%、私有工作集尽量低于 120 MB。只有实测文件支持的数据才能作为本机结果。长期稳定性需扩展至数小时/过夜、反复开合启动台和真实游戏场景。

## 项目结构与许可证

```text
src/LumaDock.App       WPF 窗口、应用控制器、主题与交互
src/LumaDock.Core      应用目录、搜索、分类和设置模型
src/LumaDock.Windows   Shell 扫描、真实图标、Win32 窗口与显示器集成
src/LumaDock.Storage   本地 JSON、备份恢复与日志
tests/LumaDock.Tests   无外部框架的可执行组件测试
tests/LumaDock.Windows.Tests  Windows Shell 只读集成测试
tests/LumaDock.Ui.Tests       真实 WPF 窗口生命周期压力测试
tests/LumaDock.Activation.Tests  单实例进程激活与隔离测试
tests/LumaDock.Backdrop.Tests    原生玻璃和最大化工作区测试
tests/LumaDock.Font.Tests        系统图标字体只读集成测试
tests/LumaDock.Layout.Tests      大字网格布局测试
tests/LumaDock.Typography.Ui.Tests 真实 WPF 字体与布局测试
tests/LumaDock.Lens.Tests        折射、柔化及图像边界测试
tests/LumaDock.Sampler.Tests     后方画面采样与窗口状态恢复测试
tests/LumaDock.Wallpaper.Tests   系统壁纸只读集成测试
scripts               构建、启动、测试、发布、性能采样
docs                  架构、验证范围、性能方法与已知限制
artifacts             本机生成的发布包与实测结果（不进 Git）
```

项目源码采用 MIT 许可证。发行包包含微软 .NET Runtime，`licenses/Microsoft.NET-LICENSE.txt` 与 `licenses/ThirdPartyNotices.txt` 复制自微软官方 SDK 附带的 .NET Library/Runtime 许可及第三方声明，项目根 `LICENSE` 仍为 LumaDock 的 MIT 许可证。应用图标版权属于各自应用所有者，运行时只读取本机资源，不随源码分发第三方品牌图标。当前无代码签名、自动更新或安装器；下载到其他机器运行时可能显示 Windows 来源警告。
