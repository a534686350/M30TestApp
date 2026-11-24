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

当前是 **v4：三段式 + 右侧竖排图标条**（照老程序 AsLab 的骨架）。
**布局细节、尺寸和硬性约定一律以 §10 为准**，这里只放一张示意图，不要重复维护。

```
┌──────────────────────────────────────────────┬────┐
│ 顶栏 46  品牌 · 方案/工位/模式 ‖ 工步 ‖ 状态·时钟·图标 │    │
├──────────────────────────────────────────────┤ ▶  │
│                                              │ ■  │
│  工作区  ContentControl Content="{CurrentView}" │ 图 │
│  （不加内边距，宽度全留给数据矩阵）                 │ 标 │
│                                              │ 条 │
├──────────────────────────────────────────────┴────┤
│ 状态栏 26  设备 LED · 系统就绪 · 版本号               │
└───────────────────────────────────────────────────┘
```

- 深色主题为近黑钢底 + 青蓝强调（`Themes/Dark.xaml`），全扁平高密度
- 标题栏时钟、F11 全屏、系统菜单弹出在 `MainWindow.xaml.cs`（纯视图行为）
- 导航唯一入口是右侧图标条；**不要再往顶栏加页签条或命令条**（v2/v3 两版都因为"顶部堆太多层"返工过）


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

- **当前取值（2026-09-30 起，两份 XAML 必须一致）**：
  `EnableRowVirtualization="True"` + `EnableColumnVirtualization="True"` +
  `VirtualizingPanel.VirtualizationMode="Recycling"` + `VirtualizingPanel.IsVirtualizing="True"` +
  `ScrollViewer.CanContentScroll="True"`。
- 为什么改成这样：原来为了保住像素级滚动把虚拟化整个关了（`IsVirtualizing=False` + `CanContentScroll=False`），
  结果 256 工位 × N 列**全部实例化**，每次切「自动测试 / 长期稳定性」页要重建整棵控件树，卡几秒。
  改成虚拟化 + 按项滚动后切页立竿见影。
- **`ScrollUnit="Pixel"` 是无效属性**：`ScrollViewer.CanContentScroll="True"` 时 WPF 按「项」滚动，
  `ScrollUnit` 只在 `CanContentScroll="False"` 时才生效。留在这里只是占位，
  **别以为现在跑的是像素滚动** —— 判断语义一律读 `ScrollViewer.CanContentScroll`，别看 `ScrollUnit`。
- **仍然生效的铁律**：`CanContentScroll="False"` 与 `IsVirtualizing="True"` / `VirtualizationMode="Recycling"`
  不可同时声明 —— 前者会整体关闭虚拟化，叠加后者 WPF 处于未定义行为，256 工位实测表现为矩阵塌缩、只渲染首行。
  也就是只能二选一：「全关虚拟化 + 像素滚动」或「开虚拟化 + 按项滚动」，不能混着写。
- 两种取值下扫码定位都不会算错：`DataGridScrollHelper.DoScroll` 已按 `CanContentScroll` 分两条路
  （为真走按项算、为假走 `TransformToAncestor` 像素算），像素分支另有 `row is null` /
  `InvalidOperationException` 兜底（行容器被虚拟化回收时退化为 `ScrollIntoView`）。
- 列宽仍用 `Auto`；动态加列由 `MatrixColumns.CollectionChanged` → `AddDynamicColumn` 触发，不要在行更新循环里改列集合结构。
- 相关取值散落在 XAML 属性，**没有** Style 兜底，两份 XAML 需同步修改。

单元格取值路径固定为 `Cells[{key}].Value`（`MatrixRowVm.Cells`，类型 `ObservableConcurrentDictionary`）。变更通知**必须**用 `Binding.IndexerName`（字面量 `"Item[]"`）：

- WPF 解析绑定路径的索引器步骤时，把该步的属性名硬编码为常量 `"Item[]"`（`PropertyPath.ResolvePathParts`），其变更监听器只对这一个名字生效。
- 发 `Item[{key}]` 这类带键名字会被 WPF **完全忽略**：除"列刚创建、绑定首次求值"那一行外，其余单元格永久停留在空白（v1.2.37 移除 `Item[]` 后曾引入此回归，v1.2.39 修复）。**不要**为"按列定向刷新"而改回带键名字。
- 代价是通知无法定向，一发即整行重估。因此更新走 `SetDeferred`（静默写入）+ `NotifyChanged`，在 `TestRunViewModel.FlushCellUpdates` 里整批写入后按行去重、每行每批发一次通知。

主题里的全局 `ScrollBar` 样式（`Light.xaml` / `Dark.xaml`）**必须按 `Orientation` 分别设置尺寸**：

- 横向滚动条的"长度"就是它的 `Width`。若对所有方向统一设 `Width`/`MinWidth`，横向条会塌成几像素的小疙瘩，无法点击拖动（v1.2.39 修复前的实际症状）。
- 正确写法：`Horizontal` 触发器设 `Height`/`MinHeight`（控制轨道粗细），`Vertical` 触发器设 `Width`/`MinWidth`。

## 10. 界面外壳与主题约定（工控 v4/v5 · 右侧图标条 + 大表优先，2026-10-29 重构）

主窗口改为**三段式 + 右侧竖排图标条**（照老程序 AsLab 的骨架：数据区占绝对主体、操作全部收到边上），
自 `MainWindow.xaml` 单文件承载，改动前先读这段：

| 段 | 尺寸 | 内容 |
| --- | --- | --- |
| Row0 顶栏 | 46 | 品牌条 + 方案/工位/设备模式胶囊 ‖ 当前工步 ‖ 运行状态、报警、时钟（`x:Name="ClockText"`）、主题 / 全屏 / 系统菜单三个图标按钮 |
| Row1 主体 | * | 左：`ContentControl Content="{Binding CurrentView}"`（**不加内边距**）；右：**60px 竖排图标条** |
| Row2 状态栏 | 26 | 设备 LED 组（`Devices`）+ 系统就绪 + 版本号 |

顶部固定 chrome 从 v3 的 132px（34+34+38+26）压到 **46+26 = 72px**，工作区拿到几乎全部宽高。

**v3 的「模块页签条 + 命令条」已整体取消**，导航全部收进右侧 60px 图标条：

- 上段**运行控制**：开始（绿 `SuccessBrush`）/ 停止（红 `ErrorBrush`），样式 `RailRunButton` / `RailStopButton`。
- 下段**视图切换**：自动测试 / 长期稳定性 / 快速测试 ‖ 手动调试 / 工位 / 日志 / 配置，样式 `RailNavButton`
  （RadioButton，每个按钮 = 17px 图标 + 9.5px 两字标签，整块 52×50）。
- 图标用 **Segoe MDL2 Assets**（Win10+ 自带），不引第三方图标库。
  **图标 `TextBlock` 的 `Foreground` 必须绑** `{Binding Foreground, RelativeSource={RelativeSource AncestorType=RadioButton}}`，
  否则会被全局 `TextBlock` 样式的 `Foreground={TextBrush}` 覆盖，选中态（白字）不生效。
- 「方案」「工位」不再占顶栏；系统菜单（数据/日志目录、方案配置、偏好、版本更新、退出）收在顶栏最右的 `&#xE712;` 图标里，
  用按钮自带的 `ContextMenu` 弹出 —— **`ContextMenu` 不在可视树内、DataContext 不继承**，必须写
  `DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}"` 桥接，否则菜单里的 Command 全是空。
- 「配置」页内部结构未变，仍是**左侧竖排子导航**（`SideTabControl` + `SideTabItem`）：
  方案 / 接口设置 / 温度采集 / 气路与参数 / 压力指令 / 设备 / 指令 / 工位 / 测试流程 / 版本信息 / 系统设置。
  子页签用 `SelectedValuePath="Header"` + `SelectedSection` 双向绑定；重命名/增删页签不影响绑定，但 `MainViewModel` 里写死的节名要同步。
- 偏好设置（语言 / 更新检查，独立的 `SettingsView`）从系统菜单进入，不占导航。

**主操作（开始 / 停止）只放在右侧图标条顶部**，页面内的 ControlBar 不再放第二组
（同时摆两组「开始 / 停止」，操作员会犹豫点哪个）。测试页 ControlBar 只留本页特有的过程控制（暂停 / 继续）+ 进度条。

### 交互反馈（hover / 按下），2026-10-29 补齐

现场反馈「鼠标放上去没特效」。原状：`GhostButton` 悬停只是把白底换成极浅的灰蓝（`NavHoverBrush`），
在白卡片上几乎看不出；**表格行/单元格完全没有悬停样式**；`CheckBox` / `RadioButton` 直接用系统默认模板
（方形直角、悬停无反馈），和整套扁平圆角风格不搭。

现在（两套主题同步）：

| 部件 | 悬停 | 按下 |
| --- | --- | --- |
| `GhostButton` / 兜底 `Button` | 底色 `AccentLightBrush` + 边框 `AccentBrush` + **文字变主色** | 底色 `AccentPressedBrush` |
| `PrimaryButton` / `SaveButton` | 底色变深 + 阴影 `CardShadowHover` | 同色 + `Opacity 0.85` |
| `DataGridCell` | 底色 `RowHoverBrush`（选中仍优先，触发器写在 `IsSelected` 之前） | — |
| `DataGridRow` | 底色 `RowHoverBrush` | — |
| `DataGridColumnHeader` | 底色 `AccentLightBrush` + 文字主色 | — |
| `CheckBox` / `RadioButton` | 边框变主色（自绘模板，圆角 3 / 圆形） | — |
| `ComboBox` / `ComboBoxItem` / `SideTabItem` / `ScrollBar` | 原本就有，未改 | — |

- 新增主题键：`RowHoverBrush`、`AccentPressedBrush`（两套主题同增）。
- **`DataGridRow` 的悬停底色只在表格没设 `RowBackground` / `AlternatingRowBackground` 时生效** ——
  DataGrid 会把这两个值直接写成行的**本地值**，本地值优先于样式触发器。我们的表格都带斑马纹，
  所以实际生效的是单元格悬停（视觉上等价，且不破坏斑马纹）。想让整行变色，就得放弃斑马纹。

### 其余页面与弹窗（2026-10-29 收尾）

- `SlotsView`（顶部「工位」页）：工具条按钮统一 28 高、表格卡换 `PanelCard`、补横向格线、去掉标题栏里的 3px 色条。
  **这一页故意不设 `RowStyle`**（没有扫码语义，全行绿底只是噪声），与配置页工位页不同，别改错。
- `SettingsView`（偏好设置）：去掉 22px 蓝色页名（改成 13.5 深色，外壳里不需要这么大）、
  去掉和页签重复的「通用 / 设备配置」大标题、卡片内边距 20,16 → 16,12、行标题 13.5 → 13。
- 小弹窗：`SlotCountDialog` / `TextInputDialog` 的「确定」改主色按钮、高度统一 30；
  `UpdateProgressWindow` 标题 20 → 15。
- `PointBatchEditorWindow` / `BulkPointEditorWindow` 里写死的 `#334155` / `#0F172A` / `White` 全部换成主题画刷
  （深色主题下这些写死色会看不清）。
- **「测试完成」「未完成测试」两个弹窗是 C# 里现搭的 Window**（`TestRunViewModel.cs`），
  但它们用 `SetResourceReference` 取主题画刷，所以切深色也跟得上 —— 不需要额外改 XAML。
- **系统 `MessageBox` 已全部替换为 `AppDialog`**（2026-10-29）：
  `Views/AppDialog.xaml(.cs)` 是自绘消息框，**重载与 `MessageBox.Show` 逐一对齐**
  （含 `Window owner` 版本和 5 参带 `MessageBoxResult defaultResult` 的版本，返回值仍是 `MessageBoxResult`），
  所以调用点只是把 `MessageBox.Show` 换成 `AppDialog.Show`，其余代码不用改 —— 全项目 45 处调用已替换。
  - 外观：标题栏左侧一个语义色点（Error 红 / Warning 琥珀 / 其它主色）+ 标题 + 正文 + 右对齐按钮；
    按钮复用应用自己的 `PrimaryButton` / `GhostButton`（88×30）。
  - **`App.xaml.cs` 里的崩溃兜底（`DispatcherUnhandledException`）继续用系统 `MessageBox`** ——
    自绘窗口若自身出错会递归，兜底链路必须是最原始的那个。
  - `AppDialog.ShowCore` 内部有 try/catch：自绘失败（或非 UI 线程、Application 尚未就绪）自动回退系统 MessageBox，
    保证保存/关机这类关键路径上的提示不会丢。
  - 自检：`--shot-dialog` 会弹一个示例框并渲染成 `shot-dialog.png`（`ShowDialog` 阻塞，所以用定时器截图）。

### 快速测试页 / 运行方案弹窗（2026-10-29）

- `QuickTestView`：参数条（压力点 · 控压 · 工位 ‖ 方案状态 + 操作）→ 左「压力控制器 / 判定指标」+
  右「工位结果」→ 测试日志 → 说明。**「单位」下拉原来只有 88px 会显示成「kP」**，已放到 104；
  左卡从 260 放到 300，判定指标不再挤成一列。
- `RunSetupWindow`：顶部标题去掉 emoji，`应用 / 取消` 统一 28 高；底部「🔄 刷新」改成「刷新工位表」。
  **弹窗里还留着 `RunBarcodeInput` / `RunScanSlotLabel` / `RunScanStatusText` / `RunSlotGrid` 这些命名元素**，
  扫码相关的事件一个都不能删（和配置页工位页同一套语义）。
- ⚠️ **主题里没有 `GroupTitle` 这个键** —— 它一直是各页在自己 `UserControl.Resources` 里定义的。
  新页面用到它必须自带一份，否则运行时报 `XamlParseException: 无法找到名为"GroupTitle"的资源`（窗口直接起不来，编译期查不出）。

### 配置页截图自检

新增通用参数 **`--shot-config-section=<节名>`**（节名 = 配置页左侧页签名，如 `接口设置` / `温度采集` / `设备`），
渲染成 `shot-config-<节名>.png` 后自动退出。加这个是因为原来只有方案 / 系统设置两个专门参数，
其余页签改完没法自检。所有 `--shot-*` 参数现在都会跳过「启动续测」弹窗（`--shot-resume` 除外）。

### 配置页（`ConfigView.xaml`，2026-10-29 起按「统一表单」排版）

- 卡片一律 `PanelCard` + `PanelTitleBar`（28px）；卡片内的分区标题用 `GroupTitle`（12px SemiBold），
  **不要再手搓「3px 色条 + SectionTitle」**。
- 表单零件（`ConfigView.Resources`，新增页面沿用即可）：
  `ConfigBtn`（28 高）/ `ConfigBtnSm`（22 高，标题栏用）/ `ConfigCheck`（等宽 108 的勾选项）/
  `GridDeleteButton`（表格行内 ×，悬停变红）。
- **字段间距统一**：标签 `FieldLabel` + 控件，字段之间 10px；双/三列靠 **Grid 空列**（8 或 12）隔开，
  不允许 `Margin="0,0,4,0"` 这种不对称写法；也不要再给单个按钮写 `Padding="12,4"`。
- 勾选项用 `WrapPanel` + `ConfigCheck`（固定宽 108），长短不一的标签也能排成整齐的列；
  `UniformGrid Columns=4` 会因为文字宽度不同而参差。
- **「当前型号指标」是标准表格**（启用 / 指标 / 代码 / Min / Max 五列），不再为每个指标套一层带底色的框。
  勾选列用 `DataGridTemplateColumn` + 绑定 `IsChecked` 的 CheckBox 实现**单击即切换**
  （`DataGridCheckBoxColumn` 要先进入编辑态，得点两下）。
- 表格里的「新增行」占位行自带一个删除按钮，视觉上像多出来的 ×：删除按钮要绑
  `Visibility="{Binding IsNewItem, RelativeSource={RelativeSource AncestorType=DataGridRow}, Converter={StaticResource BoolToVisInv}}"` 隐藏掉。
- 表格加 `BorderThickness="1" + BorderBrush={DynamicResource BorderBrush}`：卡片和表格同为白底，
  不加边框时分不清「表格空白区」和「卡片留白」。
- 顶部「快速跳转」全部用 `ConfigBtn`（原来的首项是主色按钮，看着像「当前选中页签」，有误导）。
- **2026-10-29 已全部 13 个页签过完一遍**（`指标限值` / `计算` 仍 `Collapsed`）：统一为
  `PanelCard + PanelTitleBar` 卡片头、字段「标签在上 + 10px 间距」、双列靠 12px 空列、
  按钮 `ConfigBtn` / `ConfigBtnPrimary` / `ConfigBtnSm`、表格 1px 边框 + 数值右对齐。
  各页备忘：
  - `接口设置`：串口设备（采集卡/烘箱两组并排）＋ 压力控制器；
  - `温度采集`：DMM 三字段 + 切换卡通道 4×4（用 `StackedOptionTemplate`，标签在上省宽度）；
  - `气路与参数`：阀门两列 + 温度传感器/时间参数；
  - `压力指令` / `指令`：型号区固定宽度 + 指令框两列；
  - `设备`：台数卡 / 地址表 / 通道分配三卡；
  - `工位`：扫码条（保留强调底色）＋ 表格 ＋ 右侧四卡；**行底色三态触发器和扫码相关 `x:Name`
    （`ScanSlotLabel` / `ScanProgressLabel` / `ScanStatusText` / `BarcodeInput` / `SlotGrid` /
    `OpenSlotsWindowButton`）一个都没动**，改这一页前先读本节与「扫码定位铁律」；
  - `测试流程` / `版本信息` / `系统设置`：同样三卡式；系统设置的四条路径框补了 `ToolTip` 显示完整路径
    （窄列里会被省略号截断）。

### 手动调试页（`ManualView.xaml`，2026-10-29 重排）

原版被吐槽「很乱」，根因是**没有共用样式**：卡片标题三套写法、按钮混用主色/幽灵且带 emoji（宽度全不同）、
字段靠 `Margin="0,0,4,0"` 拼双列（右列永远宽一点点）、「读取数据」是「按钮+输入框+单位」横拼（按钮文字长短不同→输入框起点不齐）、
压力卡里再套一层带底色 Border。

重排后**必须遵守的约定**：

- 卡片一律 `PanelCard` + `PanelTitleBar` + `PanelTitleText`（与测试页一致）；
  **标题栏里的右侧内容必须写 `DockPanel LastChildFill="False"` + 左右 `Dock`**，否则最后一个子元素被当成填充拉伸。
- 标题栏高 28，里面放按钮要显式 `Height="22"`（`Btn` 默认 30 会溢出）。
- 按钮只用两个样式：`Btn`（幽灵）/ `BtnPrimary`（主色），都是 **Height=30、Margin=0、居中**；
  间距交给外层 Grid 的空列（12 或 8），**不再给单个按钮写 Margin**，也**不加 emoji**。
- 字段用 `Lbl` 标签 + 控件，双/三列靠 **Grid 的空列**隔开（`Width="12"`），不要用左右不等 Margin。
- 读数项是**一张四列表**（读数项 96 / 数值 `*` / 单位 34 / 操作 70），列宽固定 → 数值和单位自然对齐；
  值用 `OutBox`（只读、Consolas、右对齐）。
- 页面结构三段（连接 / 操作 / 数据）+ 底部固定两栏子窗口（数据 I/O、历史），整页在 `ScrollViewer` 里，
  内容高度约 800，窗口不足时滚动 —— 这是内容量决定的，不是布局问题。

### 测试页工作区（v5，2026-10-29）——「一张大表 + 下方子窗口」

`TestRunView.xaml` 是**唯一**一处按「大表优先」排的页面，照 AsLab 的主界面骨架：

| 行 | 高度 | 内容 |
| --- | --- | --- |
| Row0 | Auto | ControlBar：方案 / 状态 / 耗时 + 进度条 + 暂停（`Card`） |
| Row1 | `*`（Min 200） | **采集矩阵整宽独占**（`DataMatrixGrid` / `LongTermMatrixGrid`） |
| Row2 | 6 | `GridSplitter`（横向，`ResizeBehavior=PreviousAndNext`） |
| Row3 | 176（Min 110） | 子窗口带：左「实时日志」`*` ＋ 6px 竖分隔条 ＋ 右「运行统计」300px（窄窗 236） |

- **v3/v4 顶部的 KPI 横条已删除**：工位总数 / 合格 / 警告 / 异常 / 进度 / 温度点全部搬进右下「运行统计」，
  同一组数字不再两处重复，表格因此多拿 ~46px 高度。
- 矩阵**不再被右侧 480px 日志栏长期占用宽度** —— 256 工位 × 多数据列时最缺的就是横向空间。
- 日志栏宽度改为「随窗口收窄的统计列」：`ApplyResponsiveLayout()` 里 `StatsColumn.Width`
  在 `ActualWidth < 1120` 时降到 236（原来改的是已删除的 `RightRailColumn`）。
- **矩阵是 Excel 式细格线**（`DataMatrixGrid` / `LongTermMatrixGrid`）：
  - 竖线：`GridLinesVisibility="All"` + 显式 `VerticalGridLinesBrush={DynamicResource GridLineBrush}`。
    **主题里的全局 `DataGrid` 样式把 `VerticalGridLinesBrush` 设成了 `Transparent`**（其它表格不想要竖线），
    矩阵这两个控件在自身覆盖回 `GridLineBrush` 才有竖线。
  - 横线：DataGrid 在这套主题下只画竖线（`HorizontalGridLinesBrush` 设了也不出线，实测像素级确认），
    所以矩阵各用一个局部 `MatrixCellStyle`（`BasedOn="{StaticResource {x:Type DataGridCell}}"`）
    在单元格模板里补 `BorderThickness="0,0,0,1"` 的底线。
  - 两个矩阵都要 `SnapsToDevicePixels="True"` + `UseLayoutRounding="True"`：125% 缩放下
    `RowHeight=30` 会落到半个设备像素上，横线一半清晰一半发虚（实测交替）。



视图选中态的实现方式（不要改成别的方式）：

- 图标条按钮是 `RadioButton`，样式 `RailNavButton`（`MainWindow.xaml` 的 `Window.Resources`）。
- **`IsChecked` 绑定 `MainViewModel` 的显式 bool**（`IsTabAuto` / `IsTabLongTerm` / `IsTabQuick` /
  `IsTabManual` / `IsTabLog` / `IsTabSettings`），`Mode=OneWay`，状态变化时由 `NotifyTabFlags()` 统一通知。
  三种测试都是 `SelectedNavKey == "Test"`，所以另有一个派生的 `SelectedTabKey` 区分 Auto/LongTerm/Quick。
- **绝对不要把 `IsChecked` 绑成 `Mode=TwoWay`**：`SelectedTabKey` 是只读计算属性，
  WPF 会抛 `InvalidOperationException: 无法对只读属性 SelectedTabKey 进行 TwoWay 绑定`，
  **窗口整个起不来**（只弹「未处理异常」框），而编译器不会报任何错。
  点按钮切换页面靠 `Command`，不靠回写绑定。
- 这些 RadioButton **不设 `GroupName`**：互斥已由「同一时刻只有一个 bool 为 true」保证，
  再叠加 RadioButton 组内互斥逻辑会与单向绑定打架（表现为点了不高亮）。
- 图标条的两字标签目前是硬编码中文（短标签，语言切换收益低）；要支持多语言时改用 `{DynamicResource Nav.*}`，
  键已存在：`Nav.TestRun` / `Nav.LongTermStability` / `Nav.QuickTest` / `Nav.Manual` / `Nav.Log` / `Nav.Config`。


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
