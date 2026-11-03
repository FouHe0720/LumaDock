<p align="center">
  <img src="docs/assets/cover.svg" alt="LumaDock — 原生 Windows Dock 与应用管理" width="100%">
</p>

<p align="center"><b>常用应用，放在顺手的位置。</b></p>
<p align="center">
  <a href="https://github.com/FouHe0720/LumaDock/releases/tag/v0.10.3"><img src="https://img.shields.io/badge/Release-0.10.3-588e9d?style=flat-square" alt="Release 0.10.3"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-53798a?style=flat-square" alt="Windows 10 / 11 x64">
  <a href="https://github.com/FouHe0720/LumaDock/actions/workflows/build.yml"><img src="https://github.com/FouHe0720/LumaDock/actions/workflows/build.yml/badge.svg" alt="Windows build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-7c8b82?style=flat-square" alt="MIT License"></a>
</p>
<p align="center">
  <a href="https://github.com/FouHe0720/LumaDock/releases/download/v0.10.3/LumaDock-0.10.3-win-x64.zip"><img src="https://img.shields.io/badge/Download-Windows%20x64-3d7f84?style=for-the-badge" alt="下载 LumaDock Windows 便携版"></a>
</p>
<p align="center">完整解压即可运行 · 自带 .NET Runtime · 无需管理员权限 · 预发布</p>
<p align="center">
  <a href="docs/USER-GUIDE.md">使用指南</a> ·
  <a href="docs/RELEASE-0.10.3.md">本次更新</a> ·
  <a href="docs/DEVELOPMENT.md">开发文档</a> ·
  <a href="docs/performance.md">性能记录</a>
</p>

LumaDock 是一款原生 Windows Dock 与应用管理器。常用软件固定在通透的底座上，指针经过时放大、抬起，倒影跟着变化；应用库用于查找，桌面集保留熟悉的桌面入口。

<table>
  <tr>
    <td width="33%" valign="top"><b>运行状态，看得清</b><br><br>清楚的胶囊运行标记，真实窗口识别与切换。拖动到插入标记处，把常用应用放进任意两个图标之间。</td>
    <td width="33%" valign="top"><b>桌面应用，找得快</b><br><br>应用库与桌面集分开查看，共用去重后的整理记录。搜索、手动添加、自建栏目和启动方式都留在顺手的位置。</td>
    <td width="33%" valign="top"><b>外观，自己定</b><br><br>三种轮廓、六款强调色。玻璃浓度、图标大小、悬停幅度、倒影和字体都可调整。</td>
  </tr>
</table>

## 0.10.3 · 应用库与 Dock 细节更新

恢复 **应用库／桌面集** 两个入口：前者展示已发现与手动添加的应用，后者筛选仍存在的桌面快捷方式。自建栏目、固定顺序、自选图标和原始启动参数继续保留。

这一版整理了添加应用的交互、网格空间和快捷键说明，也让 Dock 的运行状态、悬停放大与倒影更清楚。正常窗口尺寸下优先展示三排应用；缩小窗口或调大字体时，布局随可读空间调整。

![LumaDock 0.10.3 应用库：三排布局、桌面集与快捷键说明](docs/assets/library0103.jpg)
<p align="center"><sub>0.10.3 Windows 实际界面 · 真实系统应用、隔离配置与原创示例背景。</sub></p>

Dock 延续普通窗口层级。打开其他软件时，工作窗口在上方；返回桌面使用 Dock，或按 **Ctrl + Alt + Space** 打开应用库。

## 通透底座，清楚倒影

圆润的胶囊、交错的卵石、利落的轨道。当前图标随指针放大、向内抬起，相邻图标轻轻让位；停稳后动画收住。

![LumaDock 0.10.3：悬停放大与增强图标倒影](docs/assets/dock0103.jpg)
<p align="center"><sub>0.10.3 Windows 实际界面 · 真实指针悬停、90% 倒影、系统应用与原创示例背景。</sub></p>

在 **偏好设置 → Dock** 调整悬停、倒影与图标鲜艳度；在 **材质** 调整主题、强调色、浓度和图标密度。倒影强度上限提高到 **95%**，新配置默认 50%；底座浓度可低至 **2%**，图标本身保持清楚。既有偏好继续保存，按自己的壁纸明暗调整即可。

默认 **动态透景 · 清透** 由系统合成后方画面。偏爱柔和的背景，可以选择磨砂；需要减少动效，可以关闭流光或开启省电模式。支持条件见 [材质说明](docs/WINDOW-BACKDROP.md)。

**潮蓝 · 青瓷 · 琥珀 · 烟紫 · 玫瑰 · 石墨**，六款强调色用于按钮、选中状态和玻璃边缘。字体可跟随 Windows 图标设置，也可以在 LumaDock 中单独选择家族、加粗、斜体和文字颜色。[字体设置范围 →](docs/SYSTEM-FONTS.md)

## 三步开始

1. [下载 Windows 便携版](https://github.com/FouHe0720/LumaDock/releases/download/v0.10.3/LumaDock-0.10.3-win-x64.zip)，**完整解压**到准备长期保留的可写文件夹。
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
| 快速找桌面程序 | 左侧 **桌面集**，可搜索、启动或固定到 Dock |
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

重复入口按可验证的目标和系统身份整理。同一本地应用的不同启动方式收在一张卡片的右键菜单，原始参数与工作目录保留。扫描读取开始菜单、桌面与 AppsFolder 注册入口；未注册的便携软件需要手动添加，不遍历整块磁盘。

## 轻量驻留

**C# · .NET 10 LTS · WPF · Windows Shell。** 窗口状态由事件更新，图标异步读取并使用有界缓存；真实倒影复用已有图像，鲜艳度处理结果也会缓存。

指针停稳后，悬停和边缘流光停止动画。运行标记保持静态显示，不靠持续闪烁吸引注意。没有大型浏览器运行时，也没有额外后台服务。

本轮 Windows 11 短时静置实测：Dock 平均全机 CPU 约 **0.013%**，私有工作集峰值 **48.6 MB**；应用库前台约 **0.029% / 69.6 MB**。

两轮采用相同最终程序，固定 2 个真实应用，倒影 90%、底座浓度 4%。这些是指定配置下的短测，不代表所有设备或过夜驻留结果。

添加程序与快捷方式、三排布局、运行标记和前台窗口层级均已在 Windows 实际检查，另完成 100 轮管理窗口开合与搜索。[查看检查范围 →](docs/validation.md)

性能数字来自 Windows 实测，报告保留采样条件与程序集校验值。短测、交互检查与长期驻留的边界分别记录，见 [性能记录](docs/performance.md) 和 [验证范围](docs/validation.md)。

## 数据与退出

配置和轮转日志保存在 `%LOCALAPPDATA%\LumaDock`。导出、导入和恢复默认值位于 **偏好设置 → 数据**。软件不主动上传配置、应用清单或本地路径。

本版沿用配置格式 2。升级前可导出备份，原有固定项、自选图标与自建栏目继续使用。开机启动通过当前用户启动文件夹中的快捷方式实现，启用前确认。

卸载时先关闭开机启动、退出软件，再移除解压文件夹和 LumaDock 快捷方式。需要保留整理结果时保留数据目录。[完整退出、卸载与桌面恢复步骤 →](docs/USER-GUIDE.md#9-桌面图标恢复与卸载)

遇到问题，欢迎提交 [Issue](https://github.com/FouHe0720/LumaDock/issues)，写明版本、系统、缩放比例与复现步骤；分享截图或日志前请移除私人信息。

源码采用 [MIT License](LICENSE)。发行包附带微软 .NET Runtime 许可与第三方声明；运行时读取的应用名称与图标归各自所有者所有。
