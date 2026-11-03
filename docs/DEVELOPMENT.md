# 开发与构建

[返回首页](../README.md) · [使用指南](USER-GUIDE.md) · [架构](architecture.md)

## 环境

- Windows x64。WPF、Shell 与正式桌面验收必须在 Windows 执行。
- [.NET 10 SDK，Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0)。`global.json` 指定 `10.0.403`，允许同补丁系列更新。
- Git 用于克隆与版本管理；也可以下载并解压源码包。

不要求安装 Visual Studio、浏览器运行时或额外测试框架。只安装 .NET Runtime 不能编译源码；便携发行包已经包含 Runtime，普通用户不需要 SDK。

脚本优先使用项目 `.dotnet` 文件夹中的 SDK，否则使用 PATH 中的 `dotnet`。采用 SDK zip 时，将微软官方 Windows x64 SDK 完整解压到 `.dotnet`；该方式不要求修改系统 PATH。请核对官方文件来源与校验信息。

## 获取与编译

```powershell
git clone https://github.com/FouHe0720/LumaDock.git
cd LumaDock
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\start.ps1 -Launchpad
```

以上命令从项目根目录执行，不要求指定磁盘或固定文件夹。`build.ps1` 默认 Release，可加 `-Configuration Debug`。若执行策略阻止脚本，请先查看脚本内容，可对当前 PowerShell 进程临时使用 `-ExecutionPolicy Bypass`，无需修改整台设备的永久策略。

SDK 已在 PATH 时，也可直接使用命令；使用项目本地 SDK 时，将 `dotnet` 替换为 `.\.dotnet\dotnet.exe`：

```powershell
dotnet build LumaDock.slnx -c Release
dotnet run --project tests/LumaDock.Tests -c Release
dotnet run --project src/LumaDock.App -c Release -- --show-launchpad
```

WSL 可用于编辑文件，但 Linux 构建结果不能代替 Windows WPF 编译、运行或 UI 验收。

`start.ps1` 优先使用已有便携发布目录。修改源码后，可直接用上面的 `dotnet run`，或重新发布，确保运行的是新代码。

## 项目结构

| 目录 | 职责 |
| --- | --- |
| `src/LumaDock.App` | WPF 界面、Dock、应用控制器、主题和交互 |
| `src/LumaDock.Core` | 目录、搜索、分组、设置归一和图像算法 |
| `src/LumaDock.Windows` | Shell 发现、真实图标、窗口跟踪、DPI 和原生背景 |
| `src/LumaDock.Storage` | JSON 持久化、备份恢复和日志 |
| `tests` | 可执行组件测试与 Windows 集成测试 |
| `scripts` | 构建、测试、运行、发布和性能采样 |
| `docs` | 使用、架构、版本与验证文档 |
| `artifacts` | 生成的发行包和测试报告，不提交 Git |

更多边界见 [架构](architecture.md)、[Windows 集成](WINDOWS-INTEGRATION.md) 和 [窗口材质](WINDOW-BACKDROP.md)。

## 测试

| 命令 | 范围 | 环境 |
| --- | --- | --- |
| `scripts/test.ps1` | Core / Storage 契约与损坏恢复 | .NET SDK |
| `scripts/test-lens.ps1` | 像素折射、柔化、边界和取消 | .NET SDK |
| `scripts/test-layout.ps1` | 三排网格、大字自适应、视口和边界输入 | .NET SDK |
| `scripts/test-cadence.ps1` | 旧版采样节奏组件回归；实时模式不使用此策略 | .NET SDK |
| `scripts/test-icon.ps1` | 真实图标、透明通道、自定义来源、缓存更新与回退 | Windows WPF / Shell |
| `scripts/test-compositor-ui.ps1` | 实时合成、静态切换、已知背景变化与资源停止 | Windows 交互会话 |
| `scripts/test-windows.ps1` | Shell、真实图标、显示器和窗口身份 | Windows 交互会话 |
| `scripts/test-font.ps1` | 系统图标字体读取与释放 | Windows |
| `scripts/test-typography-ui.ps1` | 实际 WPF 栏目字体、文字颜色、预览、导航与流光停止 | Windows 交互会话 |
| `scripts/test-dock-ui.ps1` | Dock 四边与三轮廓、运行胶囊、放大边界、命中、滚动与动画停止 | Windows 交互会话 |
| `scripts/test-ui.ps1` | 窗口循环、重建和开合回归 | Windows 交互会话 |
| `scripts/test-activation.ps1` | 多进程单实例激活 | Windows |
| `scripts/test-backdrop.ps1` | 原生背景属性与回退 | Windows 交互会话 |
| `scripts/test-sampler.ps1` | 后方区域采样与资源释放 | Windows 交互会话 |
| `scripts/test-wallpaper.ps1` | 壁纸元数据读取与刷新 | Windows |

各脚本生成 JSON 报告至 `artifacts/tests`。交互测试不应与性能采样同时运行。测试计数、日期和边界见 [验证记录](validation.md)；不能把编译通过或短测通过写成所有硬件、游戏及长期驻留验收完成。

0.10.3 的批量添加与管理窗口检查可单独运行：

```powershell
dotnet run --project tests/LumaDock.Importer.Ui.Tests -c Release -- --report artifacts/tests/importer-ui-results.json
```

此检查使用 Windows WPF、自有程序与快捷方式文件、隔离配置，覆盖逐项失败、重复、取消、重入、三排布局与添加后的可见结果。它不操作用户原始文件，也不将内部导入入口检查视为原生文件选择器的鼠标全流程。

GitHub Actions 使用 Windows runner 构建整个解决方案，执行确定性的 Core/Storage、图像、布局与采样节奏组件测试，并生成便携包。CI 不执行真实交互桌面或性能验收，结果见 [Actions](https://github.com/FouHe0720/LumaDock/actions)。

## 生成便携包

```powershell
.\scripts\publish.ps1
```

生成目录为 `artifacts/publish`，压缩包为 `artifacts/LumaDock-版本号-win-x64.zip`。发布目标为 `win-x64` 自包含目录，不启用 WPF 裁剪，也不只输出单个 exe。完整目录包含 .NET Desktop Runtime、应用、文档和许可证。

重新构建不会自动重跑历史验收。发布前需检查版本、测试日期、二进制哈希和附件；不要打包个人配置、日志、应用清单、背景截图或 SDK。

## 创建快捷方式

完成便携发布后：

```powershell
.\scripts\create-shortcut.ps1 -Destination ([IO.Path]::Combine([Environment]::GetFolderPath('DesktopDirectory'), 'LumaDock.lnk'))
```

默认目标为项目相对位置 `artifacts/publish/LumaDock.exe`。脚本不会覆盖不属于 LumaDock 的同名快捷方式；可用 `-Executable` 和 `-Destination` 指定其他目标与位置。

## 隔离配置与性能采样

诊断参数只影响本次启动，适合避免使用日常配置：

```powershell
& .\artifacts\publish\LumaDock.exe --data-dir .\work\sandbox --diagnostics-dir .\artifacts\diagnostics --show-launchpad
.\scripts\measure-performance.ps1 -Seconds 60 -WarmupSeconds 20
.\scripts\measure-performance.ps1 -Seconds 60 -WarmupSeconds 25 -ShowLaunchpad
```

性能脚本创建独立数据目录，采样结束只停止该测试实例。第二条性能命令还需确保应用窗口真正获得前台，并核对 CSV 的 `isForeground`。不要将动态透景开启、关闭、非前台和持续交互混成一个空闲指标。

诊断模式可写出程序渲染图和计时；这些产物可能包含屏幕内容，分享前应人工检查。测量方法、CPU 口径、私有工作集与私有提交的区别见 [性能记录](performance.md)。

## 许可与贡献

源码采用 [MIT](../LICENSE)。发行包中的 `licenses/Microsoft.NET-LICENSE.txt` 与 `licenses/ThirdPartyNotices.txt` 保留微软 Runtime 许可及第三方声明。应用图标仅在运行时读取设备已有资源，不随源码分发其他应用的品牌图标。

请让修改对应清晰的行为或问题；运行适合改动的检查，并在提交中注明尚未覆盖的环境。系统设置、Shell、DPI 或窗口交互的改动需要 Windows 实机验证。
