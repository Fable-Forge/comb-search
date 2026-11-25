# Comb

Windows 上的轻量文件搜索器。第一版（P0）用本机 Everything 1.4 当引擎，通过 IPC 查询；不装服务、不要管理员。
以后计划把 Everything 换成自研引擎（MFT/USN），所以 Everything 相关代码全部集中在 `src/Engine/EverythingIndex.cs`，界面只依赖 `src/Engine/IFileIndex.cs` 里的窄接口（搜索分页、计数、列出直接子项及大小，外加引擎状态）。现在不为换引擎做更多抽象。

对外说明见 `README.md`。各自机器上的规则（本机路径、环境限制、测试记录）放在不入库的 `CLAUDE.local.md`，Claude Code 会在本文件之后自动加载它。

## 范围

- P0：托盘常驻；双击 Ctrl 呼出浮动搜索条；边输边搜的结果窗口（名称高亮、虚拟滚动、类型 / 修改时间 / 文件大小筛选与联动计数、列头点击排序、键盘鼠标操作、右键菜单、状态栏）；已索引内容概览（矩形树图 + 列表 + 面包屑钻取）；Everything 守护；托盘菜单与开机自启开关（HKCU Run）。
- 不做：拼音搜索、全文搜索、文件对话框或资源管理器内联搜索、局域网传输、插件、快速启动、收藏夹、自建索引或服务。

## 技术栈

- C# WPF 纯代码（不用 XAML），目标 .NET Framework 4.8。不依赖 NuGet 包，不需要 .NET SDK。
- 编译器用 Visual Studio 2022 Build Tools 自带的 Roslyn `csc.exe`，参考程序集用 .NET Framework 4.8 的 Reference Assemblies。路径、参数和引用列表以 `build.ps1` 为准。
- 不用 C# 中需要新运行时类型的特性（record、init、范围运算符 `..` 等），.NET Framework 4.8 没有这些类型。

## 目录约定

| 路径 | 放什么 |
|---|---|
| `src/Engine/` | 引擎层。`IFileIndex.cs`：界面唯一依赖的接口和数据类型，与引擎无关。`EverythingIndex.cs`：Everything IPC 实现，是整个项目里唯一知道 Everything（窗口类、IPC 常量、搜索语法、扩展名列表、拉起逻辑）的文件。只引用 System 系列程序集，不引用 WPF / WinForms，Probe 也编译它。 |
| `src/App/` | 界面与宿主：入口 / 单实例 / 托盘 / 开机自启、双击 Ctrl 钩子、搜索窗口（搜索条 / 结果 / 概览三种形态共用一个窗口和搜索框）、结果列表、概览页（`OverviewPage.cs`，嵌在搜索窗口里，不单独开窗口）、文件右键菜单（`ShellMenu.cs`，内置项 + Windows 原生右键菜单）、主题与格式化、exe 版本信息（`AssemblyInfo.cs`）。 |
| `tools/Probe.cs` | 命令行探针，直接调引擎打印版本、总数、耗时和前几条，用来冒烟。 |
| `tools/IconGen.cs` | 构建时生成应用图标：用文件夹黄画一个矢量放大镜，写出多尺寸的 `bin/Comb.ico`，`build.ps1` 用 `/win32icon` 把它嵌进 exe。托盘和窗口图标运行时从 exe 自身取，不另外画。 |
| `build.ps1` | 唯一构建入口。只写 ASCII（PowerShell 5.1 按 ANSI 代码页读无 BOM 脚本，中文系统上中文会吞行）。 |
| `README.md`、`LICENSE` | 对外说明和许可证（MIT）。改了功能、依赖、命令或运行要求时同步 README。 |
| `docs/` | README 用的截图（`screenshot-*.png`，窗口圆角外透明）。截图不能露出个人文件名：结果窗口用系统文件做搜索词，概览截系统目录那一层。 |
| `CLAUDE.local.md` | 各自机器上的规则，`.gitignore` 忽略，不入库。 |
| `bin/` | 构建输出（含 IconGen.exe 和生成的 Comb.ico），`.gitignore` 忽略。 |

新文件按上表归属放；要加顶层目录先改这份文档。代码注释用中文。

## 命令（Windows PowerShell 5.1，在仓库根目录执行）

- 构建：`powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`
  编译器默认用 vswhere 找最新的 Visual Studio / Build Tools 里的 Roslyn `csc.exe`，找不到时加 `-Csc <csc.exe 路径>`。Comb.exe 在运行时会占住输出文件，脚本会先检查并提示退出（托盘菜单 → 退出）。
- 冒烟：
  - `.\bin\Probe.exe ".html"`：总数、耗时、前 10 条
  - `.\bin\Probe.exe ".html" --kind doc --date week --count`：只取计数（kind = folder/file/doc/image/video，date = day/week/month）
  - `.\bin\Probe.exe ".md" --size small,medium --count`：文件大小筛选（small < 1MB / medium 1–100MB / large > 100MB，可多选，只算文件）
  - `.\bin\Probe.exe ".md" --sort type-desc --max 5`：排序（name / name-desc / path / path-desc / size-asc / size / date-asc / date / type / type-desc；size、date 是降序）
  - `.\bin\Probe.exe --children "D:\"`：直接子项及大小；不带路径则列出各盘
- 运行：`.\bin\Comb.exe`
  - 已在运行时再执行一次 = 呼出搜索窗口；`--overview` 把搜索窗口切到概览页；`--background` 静默启动（开机自启用）
- 发版：版本号只在 `src/App/AssemblyInfo.cs` 一处（`AssemblyVersion` / `AssemblyFileVersion` / `AssemblyInformationalVersion` 同步改），csc 据此生成 exe 的版本资源（资源管理器「属性 → 详细信息」）。

## 运行时约束

- 程序运行时不写任何文件（没有配置、日志、缓存）。唯一的持久化是开机自启的 HKCU Run 值 `Comb`。
- 不改 Everything 的配置（不碰 `Everything.ini`）。概览需要的「索引文件夹大小」由用户自己在 Everything 里打开（工具 → 选项 → 索引 → 索引文件夹大小），没开时界面给提示。
- 找不到 Everything 窗口时，从 `Program Files\Everything\Everything.exe`（或 x86 目录下的同名路径）用 `-startup` 拉起，再等数据库加载完。所以只需勾 Comb 的「开机自启」，Everything 自己的「随系统自启动」不必勾。
- 热键是双击 Ctrl，会和其他同样用双击 Ctrl 的工具冲突（两个都会弹出）。

## 设计约定（改界面前先看）

- 配色：中性灰白底 + Windows 蓝强调色（选中、高亮文字、比例条），窗口只留系统默认细边和圆角。表达文件夹的部分用 Windows 11 文件夹图标的黄色（从系统文件夹图标取样）：概览树图方块按大小在浅黄（文件夹黄 30% 叠白）和 `#FFD45E` 之间取色、琥珀描边，「其余」为灰色；概览列表的文件 / 文件夹 / 磁盘图标用文件夹黄线条；托盘和 exe 图标是同配色的矢量放大镜（透明底）。黄色只用在这些地方，底色和其余界面不用暖色系（米白底、柔和琥珀方块都试过，效果不好）。颜色集中在 `src/App/Theme.cs`。
- 形态切换不闪烁：搜索条 / 结果 / 概览共用一个窗口。位置大小用一次 `SetWindowPos` 设定（`SearchWindow.ApplyBounds`，设之前先按目标尺寸 `UpdateLayout`，MinHeight 摆好之后再设）；WindowChrome 只建一次，切换时只改 `ResizeBorderThickness`。从搜索条展开、首次 / 重新显示窗口时先用 DWM 隐身（`Native.SetCloaked`），等 WPF 画完两帧再现身，结果区 / 概览约 140 毫秒淡入。打字时等第一页结果回来（`ApplyPage`）再展开，按回车直接打开的不展开。不要改回分步设置 Width / Height / Left / Top：窗口会经历多次中间状态，闪烁、跳动。
- 结果和概览共用一份窗口尺寸（默认 1180×760，可拖动），互相切换时位置大小不变。
- 概览交互：树图方块和列表行都是单击选中（两边同步高亮，列表滚到该行），双击才进入文件夹 / 打开文件。双击文件时有默认打开方式就打开，没有就打开所在文件夹并选中它；窗口不隐藏。条目带所属层（`Item.Owner`），落在已刷新掉的旧条目上的点击不响应，避免面包屑重复进入同一层。扁平按钮（`FlatButton`：面包屑、返回上层、刷新）要按下和松开都在按钮上才算点击：从根层进入下一层时「返回上层」会出现在第一行的位置，只认松开的话，双击第一行的第二下松开会落在它上面，刚进去又退回来。
- 右键菜单（`ShellMenu.cs`，结果列表和概览列表共用）：上半是内置项（打开、以管理员身份打开（仅 exe/msi/bat/cmd）、打开所在文件夹、复制名称、复制完整路径、剪切、复制、删除、彻底删除，剪切 / 复制 / 删除走外壳 verb）；下半「快捷菜单」是 Windows 原生右键菜单（IContextMenu + TrackPopupMenuEx，菜单消息转给 IContextMenu3），去掉和内置重复的 open/cut/copy/delete/rename。结果列表里「打开」「打开所在文件夹」执行后收起窗口（和 Enter / Ctrl+Enter 一致），外壳操作后 1.2 秒重新搜索刷新。

## 实现要点

- `ListChildren`：Everything 按大小降序时文件整组排在文件夹前面，所以引擎把文件夹、文件分开查询各取前 N 项，再按大小合并。
- 文件大小筛选只算文件（Everything 语法 `file: size:<1mb|size:1mb..100mb|size:>100mb`），多选之间是「或」；类型 / 修改时间 / 文件大小三组计数都按另外两组的当前选择联动。
- 排序：Everything 升序时文件夹在前、降序时在后。按类型（扩展名）排序在 Everything 打开「快速扩展名排序」后明显变快。
- 复制到剪贴板用 Win32 API（`Native.SetClipboardText`，打不开时短暂重试）：WPF 的 `Clipboard.SetText` 遇到剪贴板历史等程序占着剪贴板时，文本已写入却报 CLIPBRD_E_CANT_OPEN。
- 默认打开方式用 `Native.HasDefaultOpenAction` 查关联（ASSOCSTR_COMMAND 或 ASSOCSTR_DELEGATEEXECUTE），不用 HKCR\Unknown 兜底。
- 双击 Ctrl 用低级键盘钩子，跑在独立线程上；收到睡眠唤醒广播（WM_POWERBROADCAST）后重新挂钩。

## 验证方式

- 引擎改动：构建 + Probe 冒烟，总数与 Everything 界面一致。
- 界面改动：构建 + 真窗口运行并截图，按改动走一遍：浮动条、结果窗口与筛选计数、Enter / Ctrl+Enter、概览。headless 或「构建通过」不算界面验收。
- 热键：SendInput 模拟双击 Ctrl（先退出其他用双击 Ctrl 的工具）。
- 模拟点击 / 按键和呼出窗口都会抢前台、往前台窗口送输入：有人正在用这台电脑时不要跑。测试脚本要先确认前台窗口和点击位置属于 Comb，不是就拒绝执行；多显示器缩放不同时用 per-monitor DPI 感知取坐标。

## 已知限制

- 没声明 per-monitor DPI（WPF 默认 system DPI aware），在缩放比例与主屏不同的副屏上由系统位图缩放，文字略糊。
- 界面只有中文。
- 真实睡眠唤醒后热键是否有效没验证过（只验证了模拟唤醒广播触发的重挂路径）。
