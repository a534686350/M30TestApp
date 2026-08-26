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

## 9. 已知技术债 / 待办

- [ ] ConfigViewModel 已做物理拆分（partial：主文件 + Slots + Plan + ConfigSupportViewModels）；按子模块拆成独立 Section 子 VM（需同步改写 ConfigView.xaml 绑定路径）仍待做
- [x] ~~SlotLayout Config↔RunSetup 成对重复~~ → 已抽 `Core.Config.SlotLayoutSnapshot`（板卡公式/ToOptions/ini 读写单一实现）
- [x] ~~ConfigViewModel 内点位批录解析器~~ → 已下沉 `Core.Config.PointBatchParser`
- [ ] 扫码录入 code-behind 在 ConfigView ↔ RunSetupWindow 两份，待做 attached behavior
- [x] ~~SelfUpdater 升级脚本健壮化~~ → 已完成：升级前自动备份主程序到 `rollback/previous.zip`（含版本号），设置页可「⏪回退到上一版本」（一键换装+重启，单级撤销）；剩余：sha256 校验
- [ ] AppLog 滚动与容量上限（LTS 长跑场景）
- [ ] LTS 长跑断点续测（复用 TestCheckpoint 思路）
- [ ] 单元测试覆盖：MetricsCalculator / PointBatchParser / IniFile / TaskScript.Parse
- [ ] XLSX 数值单元格（现 DataMatrix/LongTerm 导出全为 inlineStr 文本；TemplatePerformanceExporter 有正确写法）
