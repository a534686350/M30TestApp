# M30TestApp.V2 架构总览

> 留作以后查阅，编辑代码前优先校对此文件确认变更是否需要同步更新。
> 2026-08 重构后校订：新增公共助手层、UiLogBuffer、Directory.Build.props、CommunityToolkit.Mvvm。

## 1. 解决方案分层

```
┌────────────────────────────────────────────┐
│  M30TestApp.Wpf  (UI / MVVM)               │
│  ├─ Views        XAML + 代码后置            │
│  ├─ ViewModels   绑定层                     │
│  ├─ Mvvm         RelayCommand /             │
│  │               UiLogBuffer /             │
│  │               ObservableConcurrentDict  │
│  ├─ Converters   XAML 值转换器              │
│  └─ Themes       亮色/暗色主题              │
└──────────────┬─────────────────────────────┘
               │ ProjectReference
┌──────────────▼─────────────────────────────┐
│  M30TestApp.Core (业务/驱动)                │
│  ├─ Common       AppPaths/AppLog/          │
│  │               DeviceBus/SmartText/      │
│  │               GpibResource               │
│  ├─ Config       INI/CSV/Plan/Slot 解析 +  │
│  │               PointBatchParser           │
│  ├─ Devices      IDevice 抽象 + Sim/HW 实现 │
│  │               + PressureProfileApplier   │
│  ├─ TaskScript   解析 + 调度 + Action 注册  │
│  ├─ Data         DataMatrix + Cell + 指标   │
│  └─ TestSession  设备组装 + Run() 外观       │
└────────────────────────────────────────────┘
```

### 公共助手（重构新增，勿再复制粘贴）

| 助手 | 职责 |
| --- | --- |
| `Common.SmartText` | 配置文本编码统一：BOM → 严格 UTF-8 → 回退 GBK(936)；写入统一 UTF-8 BOM。IniFile/SlotTable/LegacyCsvExporter 均经此读写 |
| `Common.GpibResource` | `GPIB{port}::{addr}::INSTR` 的解析/构建（原三个 VM 各一份） |
| `Config.PointBatchParser` | 点位批量录入解析 + 压力类型显示互转（原 ConfigViewModel 内 ~350 行） |
| `Devices.PressureProfileApplier` | 手动页/快速测试页压力控制器参数写回工位并重建设备（原两份逐字相同实现） |
| `Mvvm.UiLogBuffer` | 高频日志行缓冲：任意线程 Post → Dispatcher 合批；StringBuilder 增量拼接 + 按块裁剪；`Flushed` 事件驱动滚动。ManualView 数据I/O、历史记录、TestRun 实时日志、LogView 全部经此 |

工程属性（版本号/nullable/ImplicitUsings）收敛在仓库根 `Directory.Build.props`。
WPF 工程引用 `CommunityToolkit.Mvvm` 8.4（新代码可用 `[RelayCommand]`/`[ObservableProperty]`；存量手写 Mvvm 基础设施仍有效）。

## 2. 主窗口布局（工控上位机风格）

```
┌──────────────────────────────────────────────────┐
│ 标题栏 32px: ▍M30测试专用 V1.2.36 │ 运行状态·工步·时钟│
├──────────────────────────────────────────────────┤
│ 菜单栏 31px: 系统(S) 测试(T) 视图(V) 帮助(H)        │
├──────────────────────────────────────────────────┤
│ 工具栏 48px: ▶开始测试 ■停止 │ 测试模式 │ 当前工步    │
├────────┬─────────────────────────────────────────┤
│ 扁平导航│  ContentControl CurrentView             │
│ 176px  │  （文件页签式 Tab，下沿强调条）            │
├────────┴─────────────────────────────────────────┤
│ 状态栏 28px: ●压控 ●烘箱 ●切换单元 ●板卡 ●通道板 ·就绪│
└──────────────────────────────────────────────────┘
```

- 深色主题为近黑钢底 + 青蓝强调（`Themes/Dark.xaml`），全扁平直角、1px 描边、高密度
- 菜单栏命令接线：目录打开/退出/开始停止/页面跳转/主题切换/全屏(F11)/版本信息
- 设备 LED 从独立状态带并入底部状态栏；导航为纯文本 + 左侧 3px 强调条（无 emoji）
- 标题栏时钟与 F11 全屏在 `MainWindow.xaml.cs`（纯视图行为）

导航唯一入口是左侧边栏。

## 3. 配置中心子模块

| 子页 | 数据源 | 主要控件 |
| --- | --- | --- |
| 设备 | `StationProfile.Devices` | 6 设备子 tab：型号/模式/地址/稳定参数 |
| 指令 | `CommandDictionary` | 每个型号显示 Open/SetPressure/Vent/... 模板 |
| 工位 | `SlotTable` | 256 行表格 + 新增/批量生成/导入 |
| 方案 | `TestPlan` | 基础信息 + 压力/温度点表 + 指标开关 |
| 测试流程 | `TestPlan.TaskScript` | 步骤列表 + 候选 Action 库 + 插入索引 |
| 计算 | `MetricSwitches`（VM） | 9 项指标开关 |
| 版本信息 | `CHANGELOG.md` | 当前版本 + 变更历史 |
| 系统设置 | `AppPaths` + `IniFile` | 基础路径/日志保留/主题/上次方案 |

## 4. 数据流（全自动测试）

```
TestRunView "▶开始"
  └─ TestRunViewModel.RunAsync
       └─ TestSession.RunAsync(ct)
            └─ TaskScript.Parse(plan.TaskScript)
            └─ TaskRunner.RunAsync(script, ctx, ct)
                 └─ for each TaskCommand
                      └─ IAction.ExecuteAsync(ctx, cmd, ct)
                           ├─ 设备 IO → DeviceBus.Tx/Rx
                           ├─ ctx.Matrix.Set(slot, col, value)
                           └─ AppLog.Info(...)
事件三流回 UI:
  TaskRunner.Progress  → CurrentStep/进度条
  DataMatrix.CellUpdated → TestRunViewModel 合批队列(33ms drain) → 行 Cells 更新
  AppLog.Logged        → UiLogBuffer → 日志面板
  DeviceBus.Traffic    → UiLogBuffer → 数据I/O 面板
```

## 5. 异常治理

| 层 | 兜底 |
| --- | --- |
| 命令层 | `AsyncRelayCommand` try/catch → `ErrorHandler`（日志 + MessageBox），`_running` 防重入 |
| Dispatcher | `App.DispatcherUnhandledException` → 日志 + 弹框，Handled=true |
| AppDomain | `UnhandledException` → 日志 |
| Task 调度 | `TaskScheduler.UnobservedTaskException` → 日志 + SetObserved |

## 6. 命名约定

- 矩阵列名 `<Tn><Pn>_<measure>`，如 `T1P2_Usign`。`DataMatrix.SanitizeKey` 自动把非 `[A-Za-z0-9_]` 转 `_`
- 手动采集列名 `<Label>_<measure>`，默认 Label = `MANUAL`
- CSV 导出 `data/<plan>_<yyyyMMdd_HHmmss>.csv`，行 = slot，列 = 所有出现过的列名

## 7. 设备模式切换

`Setting.ini`：

```ini
[DefaultLoadClass]
Pressure = "SIM"   ; 或 "HW"
Oven     = "SIM"

[Device.Pressure]
Model    = "FLUKE-7250"     ; 必须与 Command.ini 段名一致
Address  = "GPIB0::10::INSTR"   ; GPIB 地址可由 Config 页拆解为 板卡+地址 编辑
```

`DeviceFactory` 按 `[DefaultLoadClass]` 与 DebugMode 开关返回 SIM 或 HW 后端；SIM/HW 均完整可用。

## 8. 工位上限

256（`App.OnStartup` 中 `SlotMax`）。`[Slots] Count` 或 `工位对应表.csv` 行数 > 256 自动截断并 warn。

## 9. 矩阵表格（DataGrid）渲染约定

`DataMatrixGrid` / `LongTermMatrixGrid` 的滚动与虚拟化设置**必须自洽**，改动前先读这段：

- 取值组合固定为 `EnableRowVirtualization="False"` + `VirtualizingPanel.IsVirtualizing="False"` + `VirtualizingPanel.VirtualizationMode="Standard"` + `ScrollViewer.CanContentScroll="False"`。
- 原因：`CanContentScroll="False"`（像素级平滑滚动）会**整体关闭虚拟化**，此时若再声明 `IsVirtualizing="True"` / `VirtualizationMode="Recycling"`，WPF 处于未定义行为——256 工位实测表现为矩阵塌缩、只渲染首行。两者只能取其一。
- 选"关虚拟化 + 像素滚动"而非"开虚拟化 + ScrollUnit=Pixel"，是为了保住已调好的横向拖动滚动（`DataGridScrollHelper` 用像素偏移计算）；256 行全实例化在产线上可接受。
- 列宽仍用 `Auto`；动态加列由 `MatrixColumns.CollectionChanged` → `AddDynamicColumn` 触发，不要在行更新循环里改列集合结构。
- 相关取值散落在 XAML 属性，**没有** Style 兜底，两份 XAML 需同步修改。

单元格取值路径固定为 `Cells[{key}].Value`（`MatrixRowVm.Cells`，类型 `ObservableConcurrentDictionary`）。变更通知**必须**用 `Binding.IndexerName`（字面量 `"Item[]"`）：

- WPF 解析绑定路径的索引器步骤时，把该步的属性名硬编码为常量 `"Item[]"`（`PropertyPath.ResolvePathParts`），其变更监听器只对这一个名字生效。
- 发 `Item[{key}]` 这类带键名字会被 WPF **完全忽略**：除"列刚创建、绑定首次求值"那一行外，其余单元格永久停留在空白（v1.2.37 移除 `Item[]` 后曾引入此回归，v1.2.39 修复）。**不要**为"按列定向刷新"而改回带键名字。
- 代价是通知无法定向，一发即整行重估。因此更新走 `SetDeferred`（静默写入）+ `NotifyChanged`，在 `TestRunViewModel.FlushCellUpdates` 里整批写入后按行去重、每行每批发一次通知。

主题里的全局 `ScrollBar` 样式（`Light.xaml` / `Dark.xaml`）**必须按 `Orientation` 分别设置尺寸**：

- 横向滚动条的"长度"就是它的 `Width`。若对所有方向统一设 `Width`/`MinWidth`，横向条会塌成几像素的小疙瘩，无法点击拖动（v1.2.39 修复前的实际症状）。
- 正确写法：`Horizontal` 触发器设 `Height`/`MinHeight`（控制轨道粗细），`Vertical` 触发器设 `Width`/`MinWidth`。

## 10. 界面外壳与主题约定（浅色工控 v2）

主窗口为**五段式工控外壳**，自 `MainWindow.xaml` 单文件承载，改动前先读这段：

| 段 | 高度 | 内容 |
| --- | --- | --- |
| Row0 顶部横幅 | 42 | 品牌条 + 版本/工位胶囊 + 运行状态 LED + 报警计数 + 时钟（`x:Name="ClockText"`） |
| Row1 模块页签条 | 38 | **6 个模块页签**（`MainTab` 样式）+ 右上「系统 ▾」下拉（原菜单栏全部项收在这里） |
| Row2 命令条 | 54 | 开始测试 / 停止 + 当前测试模式 + 当前工步 + 右侧方案名 |
| Row3 工作区 | * | `ContentControl Content="{Binding CurrentView}"`，**不加内边距**，把宽度全留给数据矩阵 |
| Row4 状态栏 | 30 | 5 个设备 LED（`Devices`）+ 系统就绪 + 版本号 |

主页签只有 6 个：自动测试 / 长期稳定性 / 手动调试 / 快速测试 ｜ 日志 / 设置。
**所有配置类页面统一收在「设置」下面**（不要再往主页签加"方案""工位""配置"这类入口，否则又会出现"主页签 vs 子页签"重复）：

- 「设置」页签 → `ShowConfigCommand` → `ConfigView`，其内部是**左侧竖排子导航**（`SideTabControl` + `SideTabItem` 样式）：
  方案 / 接口设置 / 温度采集 / 气路与参数 / 压力指令 / 设备 / 指令 / 工位 / 测试流程 / 版本信息 / 系统设置。
- 子页签用 `SelectedValuePath="Header"` + `SelectedSection` 双向绑定；**重命名/增删页签不影响绑定**，但 `MainViewModel` 里写死的节名要同步。
- 原「参数控制」一页塞了 5 块内容又长又挤，已拆成 `接口设置` / `温度采集` / `气路与参数` / `压力指令` 四个子页。
- 偏好设置（语言 / 更新检查，独立的 `SettingsView`）从「系统 ▾」菜单进入，不再占主页签。
- 子页签条目多时用左侧竖排（`SideTabControl`）；条目少的页面仍用顶部横排（隐式 `TabControl`/`TabItem` 样式）。
- `指标限值`、`计算` 两个子页 `Visibility="Collapsed"`，保留待用。

页签选中态的实现方式（不要改成别的方式）：

- 页签是 `RadioButton` + `GroupName="MainNav"`，样式键 `MainTab`。
- `IsChecked` 绑定 `SelectedNavKey`，经 `Converters/NavKeyIsConverter`（`ConverterParameter` 传键名，`Mode=TwoWay`）。
  该转换器的 `ConvertBack` 在「取消选中」时返回 `Binding.DoNothing`——否则点另一个页签会把选中态写空。
- 页签文案一律走 `{DynamicResource Nav.*}`（`Strings/zh-CN.xaml` / `en-US.xaml`），**不要硬编码**，否则语言切换会失效。

主题文件（`Themes/Light.xaml` / `Dark.xaml`）的硬性规则：

- **键名只增不改不删**。视图里大量 `StaticResource`/`DynamicResource` 引用主题键，删键或改名会在运行时抛解析异常。
  新增观感只能改「取值」，或新增键（新增后两套主题必须同步补齐，键集合要一致——Dark 仅额外持有 `BgGradientStart/End`）。
- 主题切换由 `ThemeHelper.Apply` 替换合并字典实现，**不会重建视图**。因此样式内部的 Setter 一律用 `DynamicResource`
  引画刷（这样即使 Style 对象是旧的，颜色仍随主题刷新）；写死颜色字面量会导致切主题不生效。
- 外壳/页面共用样式键：`MainTab`、`PanelCard`、`PanelTitleBar`、`PanelTitleText`、`PageHeaderBar`、`PageHeaderCrumb`、`PageHeaderTitle`。
- 页面内部的每个数据面板统一用 `PanelCard` 外框 + `PanelTitleBar`(标题栏) + `PanelTitleText` + 左侧 3px `AccentBrush` 色条。

配色（v2 浅色工控）：浅钢灰底 `#EDF1F5` + 白面板 + 钢蓝强调 `#185FA5`，语义色 绿 `#16A34A` / 琥珀 `#D97706` / 红 `#DC2626`。
深色为同一套语言的 counterpart（钢灰底 `#11161C` + 钢蓝 `#3E9BE0`）。两套均为全扁平直角、高密度。

## 11. 报表模板与导出约定

测试开始时在「选择运行方案」窗口（`RunSetupWindow` / `RunSetupViewModel`）决定两件事，二者都写进 `setting/Setting.ini` 的 `[Report]`：

| 项 | ini 键 | 说明 |
| --- | --- | --- |
| 测试设备 | `DeviceName` | 报表 AO 列。UI 是可编辑 `ComboBox`（`ReportDeviceOptions`），候选来自「往期报表 AO 列」；没有的型号直接输入即可，本轮即时生效。留空回落到 `[Device.Pressure] Model`。 |
| 生成模板 | `Template` | `Auto` / `Wafer`，对应下拉 `ReportTemplateOptions`。 |

模板模式语义（`TemplatePerformanceExporter.ReportTemplateMode`）：

- `Auto`（自动测试模板）：模板取 `保存数据格式/全性能.xlsx`，导出行为与旧版一致 —— data 目录出 xlsx + 兼容 CSV，**桌面仍写旧版 `时间_型号.csv`**。
- `Wafer`（晶圆测试模板）：模板取 `保存数据格式/生成的数据格式/` 下最新的现场样例（如 `20260910 142426-08 HPT-LP-K11.1-K10-D05(性能测试).xlsx`），
  报表落 data 目录，**并在桌面另存一份同名 xlsx**，不再写旧版 CSV。

两条硬性约束：

- `ResolveTemplatePath(mode)` 首选模板缺失时会**回退到另一路**，保证现场任何情况下都拿得到模板；`ReportTemplateHint` 会把实际用到的模板文件名显示在窗口里，便于核对模板是否换成功。
- 模板文件通过 `M30TestApp.Wpf.csproj` 的 `Content` 项随包部署（`保存数据格式/全性能.xlsx` 与 `保存数据格式/生成的数据格式/*.xlsx`），改模板只换文件、不改代码。

**测试设备候选来源**（`Core.Data.ReportDeviceCatalog`）：内置默认（2025-07~12 现场 180 份报表 AO 列实测去重值）+ 只读扫描 `D:\数据整理\202507-202512`（可用 `[Report] DeviceScanDirs` 覆盖，`;` 分隔）。
扫描范围是**数据区第 4 行起**的 AO 列（第 3 行 AO 是表头「测试设备」，不能当型号收进来），结果缓存到 `setting/ReportDeviceModels.txt`。
扫描在后台线程跑，**目标目录严格只读**，不创建/修改/删除其中任何文件。

调试模式（`Setting.ini` 的 `DebugMode=1`，全部走 `Devices/Sim`）的模拟量按现场样例分布生成，见 `SimDevices.SimDac`：
桥阻 `R ≈ 6160 Ω @25℃`、`+5.2 Ω/℃`（`Isource = Usource / R`，报表 R(Ω) 列才不会空）；`Usig` 零点 `±3 mV`、灵敏度 `6.4~8.2 mV/压力单位`、幅值随温度 `-0.19%/℃`；
工位个体差异由 `SlotHash(板卡, 通道)` 决定，同一工位每次仿真给同样的值。

## 12. 已知技术债 / 待办

- [ ] ConfigViewModel 已做物理拆分（partial：主文件 + Slots + Plan + ConfigSupportViewModels）；按子模块拆成独立 Section 子 VM（需同步改写 ConfigView.xaml 绑定路径）仍待做
- [x] ~~SlotLayout Config↔RunSetup 成对重复~~ → 已抽 `Core.Config.SlotLayoutSnapshot`（板卡公式/ToOptions/ini 读写单一实现）
- [x] ~~ConfigViewModel 内点位批录解析器~~ → 已下沉 `Core.Config.PointBatchParser`
- [ ] 扫码录入 code-behind 在 ConfigView ↔ RunSetupWindow 两份，待做 attached behavior
- [x] ~~SelfUpdater 升级脚本健壮化~~ → 已完成：升级前自动备份主程序到 `rollback/previous.zip`（含版本号），设置页可「⏪回退到上一版本」（一键换装+重启，单级撤销）；剩余：sha256 校验
- [ ] AppLog 滚动与容量上限（LTS 长跑场景）
- [ ] LTS 长跑断点续测（复用 TestCheckpoint 思路）
- [ ] 单元测试覆盖：MetricsCalculator / PointBatchParser / IniFile / TaskScript.Parse
- [ ] XLSX 数值单元格（现 DataMatrix/LongTerm 导出全为 inlineStr 文本；TemplatePerformanceExporter 有正确写法）
