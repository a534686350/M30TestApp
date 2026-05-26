# M30TestApp.V2 架构总览

> 留作以后查阅，编辑代码前优先校对此文件确认变更是否需要同步更新。

## 1. 解决方案分层

```
┌────────────────────────────────────────────┐
│  M30TestApp.Wpf  (UI / MVVM)               │
│  ├─ Views        XAML + 代码后置            │
│  ├─ ViewModels   绑定层                     │
│  ├─ Mvvm         ViewModelBase/RelayCommand │
│  ├─ Converters   XAML 值转换器              │
│  └─ Themes       亮色/暗色主题              │
└──────────────┬─────────────────────────────┘
               │ ProjectReference
┌──────────────▼─────────────────────────────┐
│  M30TestApp.Core (业务/驱动)                │
│  ├─ Common       AppPaths/AppLog/DeviceBus  │
│  ├─ Config       INI/CSV/Plan/Slot 解析     │
│  ├─ Devices      IDevice 抽象 + Sim/HW 实现 │
│  ├─ TaskScript   解析 + 调度 + Action 注册  │
│  ├─ Data         DataMatrix + Cell 状态     │
│  └─ TestSession  设备组装 + Run() 外观       │
└────────────────────────────────────────────┘
```

## 2. 顶层导航

| 主菜单 | 视图 | 责任 |
| --- | --- | --- |
| 📊 实时测试 | `TestRunView` | 跑 TaskScript，看矩阵/统计/日志/控制条 |
| 🛠 手动调试 | `ManualView` | 设备控制、单/全工位多类型采集、TX-RX 跟踪 |
| ⚙ 配置中心 | `ConfigView` | 8 个子模块（见 §3） |
| 📜 日志 | `LogView` | 全量结构化日志 |

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
  DataMatrix.CellUpdated → 矩阵单元/KPI 统计
  AppLog.Logged        → 日志面板
  DeviceBus.Traffic    → TX-RX 面板 / 手动页操作记录
```

## 5. 异常治理

| 层 | 兜底 |
| --- | --- |
| 命令层 | `AsyncRelayCommand` try/catch → `ErrorHandler`（日志 + MessageBox） |
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
...

[Device.Pressure]
Model    = "FLUKE-7250"     ; 必须与 Command.ini 段名一致
Address  = "GPIB1::5::INSTR"
```

`DeviceFactory` 当前永远返回 SIM；接 HW 时按 `Profile.Backend` 走分支。

## 8. 工位上限

256（`App.OnStartup` 中 `SlotMax`）。`[Slots] Count` 或 `工位对应表.csv` 行数 > 256 自动截断并 warn。

## 9. 后续待办

- HW SCPI 驱动（基于 `Command.ini` 模板渲染 + System.IO.Ports / Ivi.Visa）
- `Cal:Test` 真实指标计算（Offset/Span/NL/TCO/TCS/TCR/TH）
- XLSX 报表导出
- 续测断点恢复（沿用原 `TempSavedDict.xml`）
- 矩阵单元越限染色（绑定 `Cell.Status` → Brush 转换器）
- 暗色主题
