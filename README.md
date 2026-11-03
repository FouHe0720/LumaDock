<p align="center">
  <img src="docs/assets/cover.svg" alt="LumaDock — 原生 Windows Dock 与应用管理" width="100%">
</p>

<p align="center"><b>常用应用，放在顺手的位置。</b></p>
<p align="center">
  <a href="https://github.com/FouHe0720/LumaDock/releases/tag/v0.10.4"><img src="https://img.shields.io/badge/Release-0.10.4-588e9d?style=flat-square" alt="Release 0.10.4"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-53798a?style=flat-square" alt="Windows 10 / 11 x64">
  <a href="https://github.com/FouHe0720/LumaDock/actions/workflows/build.yml"><img src="https://github.com/FouHe0720/LumaDock/actions/workflows/build.yml/badge.svg" alt="Windows build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-7c8b82?style=flat-square" alt="MIT License"></a>
</p>
<p align="center">
  <a href="https://github.com/FouHe0720/LumaDock/releases/download/v0.10.4/LumaDock-0.10.4-win-x64.zip"><img src="https://img.shields.io/badge/Download-Windows%20x64-3d7f84?style=for-the-badge" alt="下载 LumaDock Windows 便携版"></a>
</p>
<p align="center">完整解压即可运行 · 自带 .NET Runtime · 无需管理员权限 · 预发布</p>
<p align="center">
  <a href="docs/USER-GUIDE.md">使用指南</a> ·
  <a href="docs/RELEASE-0.10.4.md">本次更新</a> ·
  <a href="docs/DEVELOPMENT.md">开发文档</a> ·
  <a href="docs/performance.md">性能记录</a>
</p>

LumaDock 是一款原生 Windows Dock 与桌面管理工具。常用软件固定在通透的底座上，指针经过时放大、抬起，倒影跟着变化。应用库用来找软件，桌面集用来打开和整理桌面上的文件、文件夹与快捷方式。

<table>
  <tr>
    <td width="33%" valign="top"><b>运行状态，看得清</b><br><br>清楚的胶囊运行标记，真实窗口识别与切换。拖动到插入标记处，把常用应用放进任意两个图标之间。</td>
    <td width="33%" valign="top"><b>桌面文件，也在这里</b><br><br>文件夹、文档和快捷方式直接读取真实桌面。双击打开，空白处右键新建，文件变化自动同步。</td>
    <td width="33%" valign="top"><b>外观，自己定</b><br><br>三种轮廓、六款强调色。玻璃浓度、图标大小、悬停幅度、倒影和字体都可调整。</td>
  </tr>
</table>

## 0.10.4 · 桌面上的，都在桌面集

桌面集现在读取当前用户和公共桌面的实际内容。文件夹、文档、图片和快捷方式一起展示，使用 Windows 提供的真实图标；同一项目只出现一次，同名但位置不同的文件分别保留。

双击打开文件夹或文件；在空白处右键，新建文件夹或文本文档。选中项目后可以重命名、复制或复制路径，也可以将剪贴板中的文件复制到桌面。新建和重命名会作用于实际桌面，同名项目不会被覆盖。

![LumaDock 0.10.4 桌面集：真实文件夹、文件与右键新建](docs/assets/desktop0104.jpg)
<p align="center"><sub>0.10.4 Windows 实际界面 · 隔离桌面中的示例文件与文件夹。</sub></p>

桌面集可见时监听文件变化，短暂合并变更后更新；离开这一页便停止监听，不持续扫描。应用库继续专注软件清单，自建栏目、固定顺序、字体和 Dock 外观保留。

### 应用和文件，各有去处

![LumaDock 0.10.3 应用库：三排布局、桌面集与快捷键说明](docs/assets/library0103.jpg)
<p align="center"><sub>0.10.3 应用库界面参考 · 三排布局与快速启动提示继续保留。</sub></p>

应用库汇集已发现与手动添加的程序，支持搜索、多选添加和自建栏目。桌面集按实际路径展示文件，不把同名文档合并成一个项目。文件夹复制、其他文件模板及完整系统文件菜单可点击“打开桌面”使用资源管理器。

Dock 延续普通窗口层级。打开其他软件时，工作窗口在上方；返回桌面使用 Dock，或按 **Ctrl + Alt + Space** 打开应用库。

## 通透底座，清楚倒影

圆润的胶囊、交错的卵石、利落的轨道。当前图标随指针放大、向内抬起，相邻图标轻轻让位；停稳后动画收住。

![LumaDock 0.10.3：悬停放大与增强图标倒影](docs/assets/dock0103.jpg)
<p align="center"><sub>0.10.3 Dock 界面参考 · 真实指针悬停与 90% 倒影，外观设置在新版中保留。</sub></p>

在 **偏好设置 → Dock** 调整悬停、倒影与图标鲜艳度；在 **材质** 调整主题、强调色、浓度和图标密度。倒影强度上限提高到 **95%**，新配置默认 50%；底座浓度可低至 **2%**，图标本身保持清楚。既有偏好继续保存，按自己的壁纸明暗调整即可。

默认 **动态透景 · 清透** 由系统合成后方画面。偏爱柔和的背景，可以选择磨砂；需要减少动效，可以关闭流光或开启省电模式。支持条件见 [材质说明](docs/WINDOW-BACKDROP.md)。

**潮蓝 · 青瓷 · 琥珀 · 烟紫 · 玫瑰 · 石墨**，六款强调色用于按钮、选中状态和玻璃边缘。字体可跟随 Windows 图标设置，也可以在 LumaDock 中单独选择家族、加粗、斜体和文字颜色。[字体设置范围 →](docs/SYSTEM-FONTS.md)

## 三步开始

1. [下载 Windows 便携版](https://github.com/FouHe0720/LumaDock/releases/download/v0.10.4/LumaDock-0.10.4-win-x64.zip)，**完整解压**到准备长期保留的可写文件夹。
2. 双击 **LumaDock.exe**，等待首次扫描。便携包包含 .NET Desktop Runtime，普通使用无需 SDK、IDE、字体包或额外主题工具。
3. 点击 Dock 左端的 L 图标，或按 **Ctrl + Alt + Space** 打开应用库。右键常用应用 → **固定到 Dock**；未扫描到的程序通过“添加应用”选择路径。

`source.zip` 和 GitHub 自动生成的 Source code 供开发使用。源码构建需要 Windows x64 与 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
git clone https://github.com/FouHe0720/LumaDock.git
cd LumaDock
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\start.ps1 -Launchpad
```

[完整构建与发布步骤 →](docs/DEVELOPMENT.md)

## 常用操作

| 想做什么 | 从这里开始 |
| --- | --- |
| 快速启动应用 | 点击 Dock 左端 L 图标，或按 **Ctrl + Alt + Space** 打开应用库 |
| 查看桌面的文件与文件夹 | 左侧 **桌面集**，搜索名称，双击打开 |
| 新建桌面文件夹或文本 | 桌面集空白处右键 → **新建**，保存到真实用户桌面 |
| 重命名桌面项目 | 选中项目后按 **F2**，或使用右键菜单 |
| 将文件复制到桌面 | 复制文件后，在桌面集空白处右键 → **粘贴文件副本**；文件夹在资源管理器中复制 |
| 查看完整的 Windows 文件操作 | 桌面集 → **打开桌面** |
| 添加未扫描到的软件 | **添加应用**，选择程序或快捷方式；支持多选 |
| 把应用插到两个图标中间 | 拖到 Dock 的插入标记处，松开即可 |
| 把 Dock 应用放到桌面 | 拖到 Windows 桌面创建快捷方式，原文件和固定状态保留 |
| 选择其他启动参数 | 右键应用或 Dock 图标 → **启动方式** |
| 更换应用图标 | 右键 → **更换图标…**，可随时恢复原图标 |
| 建立自己的栏目 | **新建栏目**，拖入应用；右键栏目可改名或删除 |
| 调整悬停、倒影与鲜艳度 | **偏好设置 → Dock** |
| 调整玻璃、轮廓与颜色 | **偏好设置 → 材质** |
| 调整字体、粗细与文字颜色 | **偏好设置 → 字体与文字** |
| 选择第二块屏幕 | **偏好设置 → Dock → 显示器** |
| 设置开机启动 | **偏好设置 → 性能与启动**，主动启用并确认 |
| 完全退出 | Dock 空白处或托盘菜单 → **退出 LumaDock** |

应用库中的重复入口按可验证的目标和系统身份整理。同一本地应用的不同启动方式收在一张卡片的右键菜单，原始参数与工作目录保留。应用扫描读取开始菜单、桌面快捷方式与 AppsFolder 注册入口；未注册的便携软件需要手动添加。桌面集只枚举桌面顶层，不递归读取文件夹内容，也不遍历整块磁盘。

## 轻量驻留

**C# · .NET 10 LTS · WPF · Windows Shell。** 窗口状态由事件更新，图标异步读取并使用有界缓存；真实倒影复用已有图像，鲜艳度处理结果也会缓存。

指针停稳后，悬停和边缘流光停止动画。运行标记保持静态显示，不靠持续闪烁吸引注意。没有大型浏览器运行时，也没有额外后台服务。

0.10.4 的 Windows 11 短时实测：Dock 后台平均全机 CPU 约 **0.002%**，私有工作集峰值 **60.8 MB**；桌面集前台私有工作集峰值 **67.5 MB**，本轮有限采样未记录到 CPU 计时增量。

两轮采用相同最终程序，固定 2 个真实应用，倒影 90%、底座浓度 4%。桌面集包含 48 个实际项目；前台轮附加已预热的运行实例，连续采样 30 秒。CPU 未记录到增量不表示绝对零开销；这些结果不代表所有设备、持续操作或过夜驻留表现。

本版通过 193 项组件与 WPF 检查，另有 14 组批量导入、100 轮窗口生命周期及真实鼠标新建验证。桌面文件、菜单样式、键盘操作与事件释放分别检查。[查看检查范围 →](docs/validation.md)

性能数字来自 Windows 实测，报告保留采样条件与程序集校验值。短测、交互检查与长期驻留的边界分别记录，见 [性能记录](docs/performance.md) 和 [验证范围](docs/validation.md)。

## 数据与退出

配置和轮转日志保存在 `%LOCALAPPDATA%\LumaDock`。导出、导入和恢复默认值位于 **偏好设置 → 数据**。软件不主动上传配置、应用清单或本地路径。

本版沿用配置格式 2。升级前可导出备份，原有固定项、自选图标与自建栏目继续使用。开机启动通过当前用户启动文件夹中的快捷方式实现，启用前确认。

卸载时先关闭开机启动、退出软件，再移除解压文件夹和 LumaDock 快捷方式。需要保留整理结果时保留数据目录。[完整退出、卸载与桌面恢复步骤 →](docs/USER-GUIDE.md#9-桌面图标恢复与卸载)

遇到问题，欢迎提交 [Issue](https://github.com/FouHe0720/LumaDock/issues)，写明版本、系统、缩放比例与复现步骤；分享截图或日志前请移除私人信息。

源码采用 [MIT License](LICENSE)。发行包附带微软 .NET Runtime 许可与第三方声明；运行时读取的应用名称与图标归各自所有者所有。

