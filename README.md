# M30TestApp.V2

新版 M30 测试上位机（WPF .NET 8 + MVVM）。从原 WinForms 程序剥离出测试逻辑与设备指令，重写整套 UI

## 解决方案结构

```
M30TestApp.V2/
├── src/
│   ├── M30TestApp.Core/         无 UI 类库：配置/设备/脚本/数据
│   │   ├── Common/              路径与日志
│   │   ├── Config/              IniFile, CommandDictionary, SlotTable, StationProfile, TestPlan
│   │   ├── Devices/             IDevice 抽象 + Sim/* 模拟实现 + DeviceFactory
│   │   ├── TaskScript/          TaskScript / TaskRunner / 内置 Action 集合
│   │   ├── Data/                DataMatrix (slot × TnPm 矩阵)
│   │   └── TestSession.cs       上层 Facade：组装设备、运行脚本、导出 CSV
│   └── M30TestApp.Wpf/          WPF UI
│       ├── Themes/Light.xaml    亮色主题
│       ├── Mvvm/                ViewModelBase, RelayCommand, ObservableConcurrentDictionary
│       ├── ViewModels/          MainVM / TestRunVM / ManualVM / ConfigVM / LogVM / DeviceStatusVm
│       ├── Views/               TestRunView / ManualView / ConfigView / LogView + DataMatrixGrid
│       ├── App.xaml / .xaml.cs
│       └── MainWindow.xaml      顶部状态栏 + 左侧导航 + 内容区
├── samples/setting/             首次启动用的示例配置（自动复制到 bin）
│   ├── Command.ini              指令字典（多型号 SCPI/串口）
│   ├── Setting.ini              设备 SIM/HW 切换 + 端口
│   ├── 工位对应表.csv
│   └── TestConfig/demo.ini      包含 TaskScript 的测试方案
└── M30TestApp.V2.sln
```

## 运行

```powershell
dotnet build M30TestApp.V2.sln
dotnet run --project src/M30TestApp.Wpf/M30TestApp.Wpf.csproj
```

输出 EXE：`src/M30TestApp.Wpf/bin/Debug/net8.0-windows/M30TestApp.V2.exe`，旁边自动带 `setting/` 配置目录。

## TaskScript 语法

```
模块:动作[,参数1[,参数2...]]|模块:动作...
```

内置模块：

| 模块 | 动作 | 说明 |
| --- | --- | --- |
| `Initial` | `Pressure` / `Temp` / `Board` / `DMM` / `CommuTest` | 设备初始化 / 通讯自检 |
| `DAQ` | `ClearData` / `TestType,<type>` / `Down` | 清空采集矩阵 / 标记类型 / 下电 |
| `TP` | `SetPressurePoint,<idx>` / `SetTempPoint,<idx>` / `Vent` / `ReturnRoomTemp` / `StopTemp` | 测试点切换 |
| `Read` | `R` / `UT` / `Usign` / `DMMSample` | 按工位采样并写入矩阵 |
| `Save` | `TestData` | 导出 CSV |
| `Cal` | `Test` | 指标计算（占位） |

示例（demo.ini）：

```
Initial:Pressure|Initial:Board|Initial:DMM|Initial:CommuTest|
DAQ:ClearData|DAQ:TestType,测试|Read:R|Read:UT|
TP:SetPressurePoint,1,TEST|Read:Usign|
TP:SetPressurePoint,2,TEST|Read:Usign|
TP:SetPressurePoint,3,TEST|Read:Usign|
TP:Vent|Save:TestData|Cal:Test|DAQ:Down
```

## 当前进度

- ✅ 解决方案与项目搭建
- ✅ 配置加载层（INI/CSV）
- ✅ 设备抽象层 + SIM 后端 + HW 真实驱动（VISA/串口）
- ✅ TestTaskPoint 解释器 + Initial/DAQ/TP/Read/Save/Cal 内置 Action
- ✅ `DataMatrix` 实时更新事件 + CSV 导出
- ✅ WPF 亮色主题、左侧导航、设备状态条、实时矩阵表（动态列）、统计、实时日志
- ✅ 手动调试页（读压/泄压/加压/读温/设温/单工位采集、UT 电源切换）
- ✅ 配置编辑视图（方案 / 温压点 / 设备 / 指令 / 工位 / 流程 / 计算 / 系统设置）
- ✅ 设置页面（语言切换、主题、关于、GitHub 更新检测）
- ✅ UT 采集：DAQ973A 通道切换 + M30-DAC 功能码 06→05 协议
- ✅ 运行前方案与工位确认、扫码枪录入序列号、手动停止保存数据并关闭设备
- ✅ 自动测试异常自动重试，持续异常时停止并保存
- ✅ 应用名称统一为"M30测试专用"
- ✅ 指标计算 `MetricsCalculator`：Offset/Span/NL/TCO/TCS/TCR/THO/THS/PH/TCT（`Cal:Test`）
- ✅ 配置页：指标开关、系统行为、主题与方案/工位持久化
- ✅ 深色主题（设置页 + 配置页，启动时恢复）
- ✅ 续测/断点恢复（`Run:PerformanceTest`，需开启「异常退出时保留断点」）

## 待办

- [ ] 配置编辑：方案页流程开关与设备参数模板完整绑定
- [ ] 指令模板编辑写回 `Command.ini`
