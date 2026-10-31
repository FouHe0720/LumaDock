# 验证范围

验证区分组件测试、真实 Windows WPF 检查、交互检查与性能采样。编译通过不等于所有桌面环境已经验收。

## 0.8.0

Windows 11 x64 build 26300，16 个逻辑处理器，单屏 2560×1440。以下结果来自本版实际 Windows 构建与运行。

| 项目 | 结果 | 记录 |
| --- | --- | --- |
| Release 解决方案 | 19 个项目，0 警告、0 错误 | `scripts/build.ps1` |
| Core / Storage | 30/30；含清透/磨砂、窗口浓度、自定义图标保存与非法路径处理 | `artifacts/tests/results-0.8.json` |
| 图标服务 | 14/14；真实图标标志、透明通道、传统掩码、资源选择、缓存更新和并发释放 | `artifacts/tests/icon-results-0.8.json` |
| 字体与实际设置 UI | 23/23；新增更换图标的实际 VM/Image 绑定、持久化、真实恢复菜单和源文件哈希保持 | `artifacts/tests/typography-ui-0.8-results.json` |
| 系统实时合成 | 20/20；无保留快照，切换、取消、主题、失焦、隐藏、尺寸与省电回退 | `artifacts/tests/compositor-ui-0.8-results.json` |
| 动态画面实际像素 | 主窗口、Dock 各 10 次已知红蓝背景变化全部跟随；主窗保持非分层且可调整大小 | 同上 |
| Dock 回归 | 42/42；轮廓、四边、放大边界、命中、滚动、隐藏与计时器停止 | `artifacts/tests/dock-ui-0.8-results.json` |
| 原生磨砂兼容 | 4/4；保留 Acrylic、省电、恢复与工作区行为 | `artifacts/tests/backdrop-results-0.8.json` |
| Shell 集成 | 5/5；只读发现、真实图标、屏幕与窗口身份 | `artifacts/tests/windows-results-0.8.json` |
| 窗口生命周期 | 100 次开合、搜索与导航，3 次重建；关闭途中重新唤回通过 | `artifacts/tests/ui-results-0.8.json` |
| 自包含启动 | 移除 SDK 根环境变量后启动、扫描并退出；退出码 0，10.165 秒 | `artifacts/tests/portable-smoke-0.8-results.json` |
| 快捷方式 | 连续两次启动为同一驻留 PID；管理窗口可见、非分层，使用清透背景 | `artifacts/tests/shortcut-results.json` |

合成测试使用独立进程的已知颜色窗口，只记录颜色数值，不修改系统壁纸、不保存屏幕像素。实际管理窗口在可调整大小、最大化及恢复后读取非客户区实际状态，并核对没有应用持有的快照或采样计时器。颜色相位检查不能当作完整壁纸程序的端到端延迟、GPU 或帧率测量。

生命周期测试耗时 30.833 秒，强制 GC 后保留托管堆从 6,493,736 降至 4,753,264 字节（−1,740,472）。短时回收不证明不存在长期泄漏。字体、图标、合成和生命周期回归使用独立配置；自定义图标检查未更改原程序、快捷方式或图片文件，也未启动这些程序。

清透路径已在上述 Windows 11 环境验证。Windows 10、其他显卡驱动、真实混合 DPI 双屏、独占全屏、睡眠唤醒与过夜驻留仍需专项验证；系统条件不满足时保留不透明回退。资源数据见 [性能记录](performance.md)。

## 0.7.0 历史记录

环境：Windows 11 x64 build 26300，16 个逻辑处理器，单屏 2560×1440。以下为本版实际执行结果。

| 项目 | 结果 | 记录 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误；包含所有测试项目 | `scripts/build.ps1` |
| Core / Storage | 27/27；新增三轮廓、六配色及旧配置回退 | `artifacts/tests/results.json` |
| Dock 实际 WPF | 42/42；三轮廓、四边、首尾放大、最大图标、邻居联动、滚动、点击归属与计时器停止 | `artifacts/tests/dock-ui-0.7-results.json` |
| 字体与设置实际 WPF | 21/21；新增六色完整布局、实际颜色与轮廓保存；保留字体、搜索计数和流光回归 | `artifacts/tests/typography-ui-0.7-results.json` |
| 光学图像 | 14/14；增加薄条与旧像素结果的黄金样例 | `artifacts/tests/lens-results-0.7.json` |
| 光学等价比较 | 8 组固定、160 组随机，逐字节相同 | `artifacts/tests/lens-optimization-0.7.json` |
| 背景更新节奏 | 4/4；退避、变化恢复、上限及重置 | `artifacts/tests/cadence-results.json` |
| 布局 | 7/7；字号、自适应网格与边界 | `artifacts/tests/layout-results.json` |
| 原生材质 | 4/4；管理窗口非分层、DWM、省电和工作区回退 | `artifacts/tests/backdrop-results.json` |
| 区域采样 | 7/7；新增透明 Dock 窗口捕获排除与属性恢复 | `artifacts/tests/sampler-results.json` |
| 窗口生命周期 | 100 次开合与搜索，3 次重建，关闭途中唤回，通过 | `artifacts/tests/ui-results-0.7.json` |
| 自包含启动 | 移除 SDK 根环境变量后启动、扫描并退出，退出码 0，10.862 秒 | `artifacts/tests/portable-smoke-0.7-results.json` |
| 快捷方式 | 连续打开两次为同一驻留实例，管理窗口可见、非分层，原生材质有效 | `artifacts/tests/shortcut-results.json` |
| 实机视觉 | 深色卵石、浅色轨道和图标悬停，使用真实 Shell 图标与独立示例背景 | `docs/assets/dock-hover.jpg`、`dock-light.jpg` |

Dock 检查包括原生 `WindowFromPoint`：透明活动区角落穿透至下方窗口，实际底座仍命中 Dock。80 DIP 图标、最高 1.8 倍放大时，四边最外沿的 WPF 点击命中对应应用；40 条目横竖滚动后末端图标仍在窗口内。轮廓流光使用相同底座路径，布局变化时缓存，不叠加不一致的圆角静态线。

Dock、字体和生命周期回归使用隔离配置，不启动其他软件，不改 Windows 字体、启动项或桌面图标显示；快捷方式单独验证现有日常入口。生命周期轮中的动画关闭，另由 Dock 和流光测试检查动效停止；31.766 秒循环后强制 GC，保留托管堆从 12,632,936 变为 15,359,448 字节（+2,726,512）。这类短测既不能证明泄漏，也不能证明长期稳定。

本版资源数据见 [性能记录](performance.md)。没有测量持续指针输入的峰值、GPU、真实混合 DPI 多屏或独占游戏，计时器间隔不作为帧率验收。

## 0.6.0 历史记录

本节对应 0.6.0 发行包内的报告；当前生成目录中的同名文件可能已被后续测试更新。

环境：Windows 11 x64 build 26300，16 个逻辑处理器，单屏 2560×1440。

| 项目 | 结果 | 记录 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | `scripts/build.ps1` |
| Core / Storage | 25/25；合并、搜索、排序、配置恢复、字体与材质偏好 | `artifacts/tests/results.json` |
| 实际 WPF 字体与交互 | 15/15；系统字体、自选字体、字号、自适应布局、预览、搜索数量、配色与流光停止 | `artifacts/tests/typography-ui-results.json` |
| 窗口生命周期 | 100 次显示/搜索/导航/隐藏，3 次重建，关闭途中重新唤起，通过 | `artifacts/tests/ui-results.json` |
| 原生窗口材质 | 4/4；非分层窗口、原生材质读回、省电回退、恢复与工作区 | `artifacts/tests/backdrop-results.json` |
| 便携启动 | 移除 SDK 环境变量后启动、扫描并正常退出，退出码 0，约 10.01 秒 | `artifacts/tests/portable-smoke-0.6-results.json` |
| 快捷方式 | 连续打开两次仍为同一 PID、单个驻留实例；窗口正常显示，原生材质有效 | `artifacts/tests/shortcut-results.json` |
| 实机界面 | 深浅主题、应用库和名称字体分组卡已检查；展示图采用真实界面、系统应用资源和单独的示例背景 | `docs/assets/` |
| 性能短测 | Dock 与前台应用库各测量 60 秒，结果对应同一份发布二进制 | [性能记录](performance.md) |

字体及设置测试使用隔离配置，不修改 Windows 字体、启动项或桌面图标显示，不移动或删除个人文件。15 项 WPF 检查包括字体下拉、名称预览、文字入口定位以及分类数量随真实搜索结果更新。

生命周期测试关闭动画并禁用测试 HWND 输入；另有独立的动效停止回归。它不能代替连续鼠标操作、中文输入法、游戏或长期驻留检查。强制 GC 后保留托管堆从 8,396,096 变为 6,762,256 字节；短期回收不能证明没有泄漏。

## 既有组件覆盖

| 组件 | 已取得结果 | 复现脚本 |
| --- | --- | --- |
| 光学算法 | 13 项：真实像素位移、内部柔化、透明边界、取消和尺寸限制 | `test-lens.ps1` |
| 区域采样 | 6 项：取得自身窗口后的已知背景、恢复采样状态、拒绝改动其他进程窗口、GDI 资源释放 | `test-sampler.ps1` |
| Shell 壁纸读取 | 6 项：只读元数据、刷新、释放、缺失来源 | `test-wallpaper.ps1` |
| Shell 发现与图标 | 5 项：实际注册应用、图标、显示器、窗口身份 | `test-windows.ps1` |
| 系统图标字体 | 5 项：只读字体、无变化刷新、订阅释放 | `test-font.ps1` |
| 网格布局 | 7 项：字号、行高、列数、小窗口和非法值 | `test-layout.ps1` |
| 单实例激活 | 6 项：独立进程唤起、等待、数据目录隔离、超时和取消 | `test-activation.ps1` |

脚本位于 `scripts/`，报告保留实际执行日期。历史组件报告不等于所有版本都重新执行过。GitHub Actions 运行构建、Core/Storage、光学及布局测试；不运行真实桌面视觉或性能验收。

## 使用边界

- 目标为 Windows 10 / 11 x64；已验证平台是上述 Windows 11 环境。
- Windows 10、真实混合 DPI 多屏、全部主题与背景组合、独占全屏游戏、睡眠唤醒和数小时/过夜驻留仍需专项验证。
- 运行圆点和窗口切换依赖可读取的真实窗口身份；受保护或跨权限应用可能无法匹配。
- 动态透景与第三方截图或录屏并发时，画面可能短暂漏掉自身窗口；稳定截图前可关闭动态透景。
- 性能结果只对应记录的二进制、状态和测试时长，不承诺 GPU 占用或 60 fps。
- 暂无桌面时钟、分组标签改名/排序、代码签名、安装器或自动更新。

具体环境要求见 [使用指南](USER-GUIDE.md)，材质回退见 [窗口背景](WINDOW-BACKDROP.md)，资源数据见 [性能记录](performance.md)。
