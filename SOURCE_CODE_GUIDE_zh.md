# VPet-AI 源码分析与二次开发指南（面向开发者）

> 目标：帮助你在不了解项目背景的情况下，快速理解架构、运行时序、存档/配置格式、MOD/插件扩展点，以及常见改动应该落在哪些文件。
>
> 说明：本指南基于仓库当前源码结构进行“从代码出发”的梳理，尽量少复述 README，而是把核心调用链、关键类职责、数据/线程边界讲清楚。

---

## 1. 技术栈与总体形态

- **平台**：Windows 桌面应用
- **UI 框架**：WPF（`UseWPF=true`），并混用 WinForms（系统托盘 `NotifyIcon` 等，`UseWindowsForms=true`）
- **目标框架**：主要项目是 `.NET 8 (net8.0-windows)`
- **依赖库（重点）**
  - `LinePutScript`：自定义的轻量文本数据格式/解析/序列化（设置、存档、MOD 配置大量使用）
  - `LinePutScript.Localization.WPF`：本地化与 `Translate()` 扩展
  - `Panuon.WPF.UI`：WPF UI 控件/弹窗/主题支持
  - `SkiaSharp`：图形相关（缓存/渲染链条中会用到，具体实现分散在 Graph 子模块）
  - `NAudio`：音频/音乐识别/播放相关（Windows 项目中）
  - `Facepunch.Steamworks`：Steam 接入 + Workshop MOD 路径枚举

---

## 2. 解决方案与项目拆分

解决方案文件：`VPet.sln`

### 2.1 项目一览（建议先看这几个入口）

1. **VPet-Simulator.Windows**（主程序，WPF 可执行）
   - 入口：`VPet-Simulator.Windows/App.xaml.cs`（异常处理、启动参数、多存档扫描）
   - 主窗体：`VPet-Simulator.Windows/MainWindow.xaml(.cs)` 与 `MainWindow.cs`（大量逻辑拆分在 `.cs` partial 中）

2. **VPet-Simulator.Core**（核心运行时：动画/图形、触摸区域、核心数据容器）
   - `GameCore`：把 **控制器、图形核心、存档** 三者聚合
   - `Main`（WPF 控件）：宠物显示、定时器、触摸/移动/说话等主循环
   - `Graph/*`：动画与图形系统（缓存、查找、解析、运行）
   - `Handle/*`：基础模型/接口（例如 `IController`, `IGameSave`, `IFood`, `PetLoader` 等）

3. **VPet-Simulator.Windows.Interface**（对外“接口层”，给 MOD/插件用）
   - `IMainWindow`：主程序暴露给插件的能力（存档/设置/资源/事件/窗口等）
   - `ISetting`：设置接口（实现位于 Windows 项目）
   - `MainPlugin`：代码插件基类与生命周期
   - `GameSave_v2`、`Statistics`、`ScheduleTask`、`Food/Photo` 等：作为 MOD 数据结构与 API 的一部分

4. **VPet-Simulator.Tool**（工具项目，.NET Framework 4.8 控制台）
   - 目前主要用于处理动画帧（去重/重命名），见 `VPet-Simulator.Tool/Program.cs`

5. **VPet.Solution**（另一个 WinExe 项目）
   - 同样引用 Core + Interface，可能用于另一套 UI/壳或试验性/抽离式解决方案（按需深挖）

---

## 3. 程序启动与主流程（非常重要）

把“桌宠怎么跑起来”的链路讲清楚，后面开发改哪里会非常快。

### 3.1 App 启动：参数、多存档

入口：`VPet-Simulator.Windows/App.xaml.cs`

- 记录 `Args`（命令行参数）
- 扫描运行目录下 `Setting*.lps`：
  - 解析得到可用的多存档前缀列表 `MutiSaves`
  - 这是“多开/多存档”的基础
- 处理 `startup_*` 文件（会注入 `prefix#...` 参数）
- 非 DEBUG 下会注册全局异常捕获，并对“常见错误类型”给出用户提示（也会尝试识别是否由 MOD 导致）

### 3.2 MainWindow 构造：环境、Steam、MOD 路径

入口：`VPet-Simulator.Windows/MainWindow.xaml.cs` + `MainWindow.cs`

典型顺序（按实际代码）：

1. **解析 Args**（存入 `LPS_D` 结构）
2. **存档前缀**：如果存在 `prefix` 参数，会设置 `PrefixSave`（例如 `-A`），决定读取/写入哪个 `Setting{Prefix}.lps` 和 `Save{Prefix}_*.lps`
3. **BaseDirectory**：运行目录通过 `Assembly.Location` 推导，写入 `ExtensionValue.BaseDirectory`
4. **本地化**：配置 `LocalizeCore.TranslateFunc`，并在稍后 `GameLoad` 里加载语言包
5. **Steam 初始化**：尝试 `SteamClient.Init(...)`，成功则 `IsSteamUser=true`
6. **旧目录迁移**：例如 `BackUP -> Saves`
7. 调用 `GameInitialization()`（读取设置、初始化 WPF 窗口、检查核心模组存在）
8. 异步启动 `GameLoad(modPaths)`：
   - `modPaths` 来自本地 `mod` 目录
   - 若 Steam 用户，会额外枚举 Workshop 已安装内容并追加目录

### 3.3 GameInitialization：读取设置、创建窗口、控制器绑定

实现：`VPet-Simulator.Windows/MainWindow.cs` 的 `GameInitialization()`

关键点：

- 读取设置：`Setting{PrefixSave}.lps`（主档为空前缀时还会使用 `Setting.bkp` 作为坏档回退）
- 初始化窗口与布局：
  - `InitializeComponent()`
  - 根据设置应用 `ZoomLevel`、透明度、位置（支持“记录上次位置/启动位置”）
- 创建窗口控制器：`MWController`，并赋给 `Core.Controller`
  - 这一步是 Core 与 Windows 层“交互/窗口行为”的桥梁
- 检查核心模组：强依赖 `mod\0000_core\pet\vup` 存在，否则直接弹窗并关闭

### 3.4 GameLoad：加载 MOD、语言、存档、图形、UI、插件

实现：`VPet-Simulator.Windows/MainWindow.cs` 的 `GameLoad(List<DirectoryInfo> Path)`

这段是整个项目的“装载”阶段，重点包括：

1. **枚举 MODPath 并构造 CoreMOD**
   - 每个 MOD 目录只要存在 `info.lps` 就会进入加载
   - `CoreMOD` 会解析目录结构（见下方“MOD 系统”章节）并把内容灌入主程序：
     - Pets、Themes、Foods、Photos、文本资源、语言包、插件 DLL 等

2. **缓存清理策略**
   - `GraphCore.CachePath = <运行目录>\cache`
   - 若 `Set.LastCacheDate < CoreMODs.Max(CacheDate)`，会清空 cache（MOD 更新可能需要重建缓存）

3. **语言加载与旧设置兼容**
   - `Set.Language` 为 `null` 时加载默认文化
   - 兼容旧版 `CGPT` 设置字段（把 enable 转成 type 等）

4. **选择当前宠物 PetLoader**
   - 根据 `Set.PetGraph` 匹配 `Pets` 列表
   - 找不到则用 `Pets[0]`

5. **存档加载与回退**
   - 优先加载旧 `Save.lps`（legacy），失败则 `LoadLatestSave(petloader.PetName)`
   - `LoadLatestSave` 会从 `Saves\Save{PrefixSave}_*.lps` 倒序尝试，失败再新建 `GameSave_v2`
   - 存档与备份不一致会提示用户

6. **创建图形核心与 Main 控件（核心渲染循环）**
   - `Core.Graph = petloader.Graph(Set.Resolution, Dispatcher)`：加载动画并准备缓存
   - `Main = new VPet_Simulator.Core.Main(Core)`：Core 层 WPF 控件
   - `Main.LoadALL(...)`：
     - 创建 UI 控件（ToolBar/MessageBar/WorkTimer）
     - 绑定定时器（移动、事件、智能移动等）
     - 等待动画资源就绪（`Load_2_WaitGraph`）
     - 开机动画/默认动画并进入工作状态

7. **UI 与功能绑定（Windows 层）**
   - 初始化 ScheduleTask（日程表）、构建工作/学习/娱乐菜单
   - `LoadTheme(Set.Theme)` / `LoadFont(Set.Font)`
   - 初始化托盘图标、右键菜单
   - 加载聊天 UI（TalkBox/TalkAPI）

8. **插件生命周期调用（非常关键）**
   - 在 UI 初始化阶段：对每个 `Plugins` 调用 `mp.LoadPlugin()`
   - 完成错误提示/动画检查后：再对每个 `Plugins` 调用 `mp.GameLoaded()`

---

## 4. Core 核心结构：GameCore / Main / Graph

### 4.1 GameCore：核心聚合对象

文件：`VPet-Simulator.Core/Handle/GameCore.cs`

- `IController Controller`：平台/窗口能力抽象（由 Windows 项目提供实现）
- `GraphCore Graph`：图形与动画系统
- `IGameSave Save`：当前宠物存档（游戏状态）
- `List<TouchArea> TouchEvent`：触摸区域定义（由 GraphConfig 或运行时插入）

### 4.2 Main（Core.Display）：宠物显示与主循环

文件：`VPet-Simulator.Core/Display/Main.xaml.cs`（这是一个 WPF 控件，不是 `static Main`）

Main 的职责可以理解为：

- 在 WPF 视觉树中承载宠物渲染区域
- 管理主定时器（事件 Tick、移动 Tick、智能移动 Tick 等）
- 管理 ToolBar / MessageBar / WorkTimer
- 基于 `Core.Graph` 查找并运行动画（开机/呼吸/移动/触摸/说话/工作等）

重要方法：

- `LoadALL(...)`：把 “创建 UI → 绑定事件 → 等待动画 → 启动” 串成一条
- `Load_2_WaitGraph(...)`：并发等待所有 `IGraph.IsReady`，收集失败信息
- `Load_4_Start(...)`：找 `StartUP` 或 `Default` 动画并进入工作态

线程注意：

- Main 内部既有 UI 线程 Dispatcher，也有大量 Task/Timer 回调；
- 绝大多数 UI 操作都必须 `Dispatcher.Invoke`/`InvokeAsync`。

### 4.3 GraphCore：动画索引与查找

文件：`VPet-Simulator.Core/Graph/GraphCore.cs`

核心数据结构：

- `Dictionary<GraphType, HashSet<string>> GraphsName`：按“动画类型”索引可用名字集合
- `Dictionary<string, Dictionary<AnimatType, List<IGraph>>> GraphsList`：按“名字 → 动作 → 动画列表”存储

查找逻辑：

- `FindName(GraphType type)`：随机返回某个名字
- `FindGraph(name, animat, mode)`：
  - 优先命中当前 `mode`（Happy/Nomal/PoorCondition/Ill）
  - 找不到会做上下兼容（例如向上/向下找相邻状态）
  - 最后退回“非 Ill 的随机”

缓存：

- `GraphCore.CachePath = <运行目录>\cache`
- 主程序会在 MOD 更新时主动清空 cache。

### 4.4 GraphInfo：动画类型/动作/状态与命名规则

文件：`VPet-Simulator.Core/Graph/GraphInfo.cs`

- `GraphType`：定义了系统内会被默认使用的动画类型（StartUp / Default / Move / Work / Say / Touch 等）
- `AnimatType`：`Single` 或者三段式 `A_Start/B_Loop/C_End`
- `ModeType`：来自 `IGameSave.ModeType`

还支持从**文件路径 + info** 推导动画信息（用于自动加载/路径约定），路径中出现 `happy/nomal/poorcondition/ill` 也会被识别成状态。

---

## 5. PetLoader：宠物包与动画加载

文件：`VPet-Simulator.Core/Handle/PetLoader.cs`

- 一个 PetLoader 对应一个“宠物定义”，它可以来自多个 mod（`path` 允许追加）
- `Graph(int resolution, Dispatcher dispatcher)`：创建新的 `GraphCore` 并加载所有动画

加载策略：

- 若目录含 `info.lps`：按配置逐条加载（支持多种 graph 类型）
- 若目录无 `info.lps`：
  - 只有 1 张图 → 当作 `Picture`
  - 多张图/目录 → 当作 `PNGAnimation`

可扩展点：

- `PetLoader.IGraphConvert`：支持注册新的图像加载器（键为字符串，如 `pnganimation/picture/foodanimation`）
  - 这是“新增一种动画/渲染格式”的核心入口之一。

---

## 6. 设置系统（Setting）

文件：`VPet-Simulator.Windows/Function/Setting.cs`

设计特点：

- `Setting : LPS_D, ISetting`：本质上是一个可序列化的键值/层级结构（文本格式），外面包了一层强类型属性
- 很多布尔项是以“反向命名”存储（例如 `TopMost` 属性内部写的是 `topmost` 的取反），改动时要小心

关键设置项（举例）：

- `ZoomLevel`：缩放倍率（影响窗口宽度、helper 尺寸等）
- `Language` / `Theme` / `Font`
- `AutoSaveInterval`、`BackupSaveMaxNum`
- `LastCacheDate`：用于判断是否清空动画 cache
- `PressLength`、`InteractionCycle`、`LogicInterval`

文件落盘：

- 主窗口 `MainWindow.Save()` 会写：`Setting{PrefixSave}.lps`
- 对默认前缀还会做 `Setting.lps -> Setting.bkp` 的备份策略

---

## 7. 存档系统（GameSave_v2 / SavesLoad / 备份）

### 7.1 存档结构

- `GameSave_v2`（Interface 项目）：
  - `GameSave`（当前宠物的核心存档，类型为 `GameSave_VPet`）
  - `Statistics`（统计/成就/时长等）
  - `Data`（杂项数据，扩展字段放这里）
  - `HashCheck`（反作弊/一致性校验标志）

- `GameSave`（Core 项目）：
  - 基础数值（Money/Exp/Strength/Feeling/Health/Likability 等）和部分计算逻辑

### 7.2 SavesLoad：读档过程与保护

文件：`VPet-Simulator.Windows/MainWindow.cs` 的 `SavesLoad(ILPS lps)`

- 将 `ILPS` 转为 `GameSave_v2`，并对明显坏数据做拦截：
  - 关键数值全为 0 → 视为异常
  - Exp/Money 过小导致溢出风险 → 自动回正并提示
- 兼容旧版 `Set.PetData_OLD` 的结构迁移

### 7.3 Save：写档与备份策略

文件：`VPet-Simulator.Windows/MainWindow.cs` 的 `Save()`

- 保存顺序：
  1) `ScheduleTask.Save()`
  2) 遍历 `Plugins` 调 `mp.Save()`
  3) 写 `Setting{PrefixSave}.lps`
  4) 写存档：
     - 正常存档：`Saves\Save{PrefixSave}_{st}.lps`（`st` 是 `Set.SaveTimesPP` 自增值）
     - 备份存档：`Saves_BKP\Save{PrefixSave}_{hash:X}.lps`（hash 来自 `GetHashCode()%255` 的简化桶）
     - 删除多余旧存档（超过 `BackupSaveMaxNum`）
  5) 旧版 `Save.lps` 会移动到 `Save.bkp`

### 7.4 插件关闭/退出

- 窗口关闭与重启时会调用每个插件：`mp.EndGame()`
- 然后调用 `Save()`
- `Exit()` 会停止所有 `IGraph`、清理窗口/托盘/Steam 连接等，并最终 `Environment.Exit(0)`

---

## 8. MOD 系统（CoreMOD）

文件：`VPet-Simulator.Windows/Function/CoreMOD.cs`

### 8.1 MOD 文件夹结构（从代码推导）

MOD 根目录（例如 `VPet-Simulator.Windows/mod/xxxx_xxxx`）需要：

- `info.lps`：MOD 元信息（名称、作者、版本、游戏版本、cachedate、可选的语言条目等）

支持的子目录（按 `switch (di.Name.ToLowerInvariant())`）：

- `theme/`：主题
  - `*.lps`：主题定义（`Theme`）
  - `fonts/*.ttf`：主题字体
  - 主题图片包会按 `Theme.Image` 对应的子目录加载

- `pet/`：宠物定义（`*.lps`，首行 `pet`）
  - 会创建/合并 `PetLoader`
  - 支持多 MOD 对同名宠物追加动画路径与配置

- `food/`：食物（`*.lps`，多条 `food`）

- `image/`：图片资源
  - 直接加载 `*.png`
  - 递归加载子目录，并用 `folder_file` 的方式拼 key
  - 也支持 `*.lps`：
    - `set_*.lps` 进入 `ImageSources.ImageSetting`
    - 其他 `*.lps` 作为图片清单批量导入

- `file/`：文件资源（递归加载，key 为文件名/目录前缀拼接）

- `photo/`：照片/图库定义（`photo` 行）

- `text/`：文本资源（低饥饿/低口渴/点击/选择/日程包等）

- `lang/`：语言包（按文件名或子目录名作为 culture key）

- `plugin/`：代码插件（DLL）
  - `*.dll`：包含继承 `MainPlugin` 的导出类型会被反射加载
  - 可选 `load.lps`：控制 skip/cpu/ignoreError

### 8.2 MOD 启用/停用、信任与安全

- 默认自动启用的 MOD 名称白名单：`Core`, `PCat`（`OnModDefList`）
- 设置里保存：
  - `onmod`：启用列表
  - `passmod`：允许加载代码插件（未签名/不受信任时）
  - `msgmod`：用于只提示一次“该 mod 包含代码插件但未允许”

插件 DLL 安全策略（Release 下更严格）：

- 若 DLL 具备特定签名证书（LBGame 证书），视为可信
- 若没有可信签名，且用户未在设置里 `passmod`，则不加载（`SuccessLoad=false`）

这意味着：

- 你开发自己的代码插件时，**可能需要用户在设置里显式允许该 MOD 的代码插件** 才能在 Release 版本加载。

### 8.3 代码插件如何被发现

- `Assembly.LoadFrom(dllPath)`
- `dll.GetExportedTypes()`
- 找 `exportedType.BaseType == typeof(MainPlugin)`
- `Activator.CreateInstance(exportedType, mw)` 创建实例并加入 `mw.Plugins`

---

## 9. 插件系统（MainPlugin / IMainWindow）

### 9.1 生命周期（按实际调用顺序）

1. `new Plugin(mainWindow)`：**构造函数阶段**
   - 文档注释强调：此时不要访问 Core/Save/UI（很多还没准备好）

2. `mp.LoadPlugin()`：在 UI 初始化时调用（位于 `GameLoad` 的 UI Dispatcher 段中）
   - 适合做：
     - 订阅 `Main` 的计时器/事件
     - 创建自己的 WPF 控件/窗口
     - 注册自定义按钮（也可以在 `LoadDIY` 钩子里）

3. `mp.GameLoaded()`：游戏完全加载并完成错误检查后调用
   - 适合做：
     - 修改已加载的数据（Foods/Pets/GraphConfig 等）
     - 读取/修复存档中的自定义字段

4. `mp.Save()`：每次 `MainWindow.Save()` 都会调用
   - 推荐把数据写入 `MW.GameSavesData.Data`（或你自己的文件资源区）

5. `mp.EndGame()`：窗口关闭/重启时调用

### 9.2 IMainWindow：插件能拿到什么

接口：`VPet-Simulator.Windows.Interface/IMainWindow.cs`

你能拿到（举例）：

- `Core`（含 `Save`、`Graph`、`TouchEvent`）
- `Main`（核心显示控件，含计时器/事件/动画 API）
- `Set`（设置接口）
- `Foods/Photos/Texts` 等资源
- `DynamicResources`（主程序提供的共享字典，适合插件间共享数据）
- 存档相关：`GameSavesData`、`HashCheck`、`HashCheckOff()`（作弊/超模插件必须主动关闭 HashCheck）
- 窗口相关：`Windows` 列表（退出时会统一关闭）、`MouseHitThrough` 等
- 联机相关事件：`MutiPlayerHandle` / `MutiPlayerStart`（如果你要做联机功能）

---

## 10. 多开/多存档机制（PrefixSave）

- 启动参数中出现 `prefix` 会切换存档前缀：例如 `-A`
- 设置文件变为：`Setting-A.lps`
- 存档文件变为：`Saves\\Save-A_<n>.lps`
- App 会扫描 `Setting*.lps` 来生成 `MutiSaves` 列表
- UI 里 `LoadDIY()` 会在“DIY”菜单生成“桌宠多开”的入口

---

## 11. Steam 与 Workshop（简述）

- 初始化：`SteamClient.Init(appId, true)`
- Workshop：通过 `Steamworks.Ugc.Query.ItemsReadyToUse` 分页枚举目录并加入 MOD 搜索路径
- Rich Presence：在 `Main.TimeHandle` 中更新（例如用户名、状态、等级等）

开发提示：

- Windows 项目 `csproj` 里对 x86/x64 分别引用不同 Steamworks 包；调试时请选对应平台。

---

## 12. 二次开发常见目标：改哪里

### 12.1 想新增一种“动画资源格式/加载器”

- 优先看：`VPet-Simulator.Core/Handle/PetLoader.cs`
  - 在 `PetLoader.IGraphConvert` 注册新类型字符串 → `LoadGraphDelegate`
- 同时你可能需要实现：
  - 新的 `IGraph` 实现（在 `VPet-Simulator.Core/Graph/*` 下找现有实现参考：`PNGAnimation`, `Picture`, `FoodAnimation`）

### 12.2 想给 MOD 增加新的目录类型（例如 `sound/`）

- 改动点：`VPet-Simulator.Windows/Function/CoreMOD.cs`
  - 在 `switch (di.Name.ToLowerInvariant())` 增加新 case
  - 决定资源落到 `MainWindow` 的哪个容器：`ImageSources/FileSources/DynamicResources` 等
- 同时考虑：
  - 是否需要把能力开放到 `IMainWindow`（如果希望插件/外部调用）

### 12.3 想改存档结构 / 增加自己的字段

推荐：

- 使用 `GameSave_v2.Data` 写自定义数据（LPS 结构，支持层级）
- 在插件里：
  - `GameLoaded()` 读取/迁移
  - `Save()` 写回

不推荐：

- 直接修改 `GameSave_VPet` 的强类型字段（会影响兼容与旧档迁移）

### 12.4 想改 UI（菜单、设置页、托盘菜单）

- 主菜单/按钮添加：主要在 `VPet-Simulator.Windows/MainWindow.cs` 的 `GameLoad` UI 段
- 设置窗口：`VPet-Simulator.Windows/WinDesign/winGameSetting.xaml(.cs)`
- 托盘菜单：`VPet-Simulator.Windows/MainWindow.cs` 的 `notifyIcon.ContextMenuStrip` 构建

### 12.5 想做“作弊/超模”类 MOD

- 代码中有 HashCheck 保护：`GameSave_v2.HashCheck`
- 如果你的 MOD 会改动存档数值（尤其是超模），请在插件执行作弊前调用：
  - `IMainWindow.HashCheckOff()`

否则：

- Steam/统计/部分 UI 会把该玩家标记为未通过 HashCheck
- 某些异常处理会提示“超模导致溢出”

---

## 13. 构建与运行（开发者建议流程）

1. 安装：
   - Visual Studio 2022
   - .NET SDK 8.x
   - 若要编译 `VPet-Simulator.Tool`：安装 .NET Framework 4.8 Developer Pack

2. 在 VS 中打开 `VPet.sln`

3. 选择启动项目：
   - 一般运行桌宠：`VPet-Simulator.Windows`

4. 选择平台：
   - `x64` 或 `x86`（与 Steamworks 包/steam_api dll 对应）

5. 首次启动常见坑：
   - 缺少 `mod\\0000_core\\pet\\vup` 会直接报错无法启动（这是强依赖）
   - cache 损坏/被清理：删除运行目录下 `cache` 可触发重建

---

## 14. 词汇表（快速对齐概念）

- **LPS / LinePutScript**：项目大量使用的文本数据格式（类似 ini + 层级 + 列表），用于 Setting、Save、Mod 配置
- **MOD**：以目录形式装载的内容包（pet/theme/food/text/lang/plugin 等）
- **插件（代码插件）**：MOD 的 `plugin/` 目录里的 DLL，导出继承 `MainPlugin` 的类型
- **Graph**：动画/图像资源的抽象，`IGraph` 负责加载/缓存/运行到 WPF 容器
- **GraphType**：动画类型分类（Default/Move/Say/Work/StartUp...）
- **AnimatType**：动画阶段（Single 或 Start/Loop/End）
- **ModeType**：宠物状态（Happy/Nomal/PoorCondition/Ill）

---

## 15. 下一步你可以怎么用这份指南

- 如果你的目标是“做一个新功能”，建议你先回答这 3 个问题：
  1) 它更像 **插件**（可独立分发，最好不改主程序）还是 **主程序功能**（需要改 Windows 项目）？
  2) 它需要新的 **资源类型**（改 CoreMOD）还是只是用现有资源（写插件即可）？
  3) 它要不要写入 **存档/设置**（写入 GameSave_v2.Data 或 Setting）？

如果你愿意，我也可以基于你的具体开发目标（比如“新增一种互动动作/新增一个工作类型/新增一个 UI 面板/写一个示例插件”）直接帮你把改动点落到具体文件与函数上，并给出最小可行的实现步骤。
