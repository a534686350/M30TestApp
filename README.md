# M30TestApp.V2

新版 M30 压力传感器测试上位机（WPF .NET 8 + MVVM）。从原 WinForms ASLab 程序重写，保留全部测试逻辑与设备指令协议。

## 解决方案结构

```
M30TestApp.V2/
├── src/
│   ├── M30TestApp.Core/         无 UI 类库：配置/设备/脚本/数据
│   │   ├── Common/              路径、日志、全局偏好、设备总线
│   │   ├── Config/              IniFile, CommandDictionary, SlotTable, StationProfile, TestPlan
│   │   ├── Devices/             IDevice 抽象 + Sim/* 模拟 + Hw/* 真实驱动 + DeviceFactory
│   │   ├── TaskScript/          TaskScript / TaskRunner / 内置 Action 集合
│   │   ├── Data/                DataMatrix, MetricsCalculator, TestCheckpoint
│   │   └── TestSession.cs       上层 Facade：组装设备、运行脚本、导出 CSV
│   └── M30TestApp.Wpf/          WPF UI
│       ├── Themes/              Light.xaml / Dark.xaml / ThemeHelper
│       ├── Mvvm/                ViewModelBase, RelayCommand
│       ├── ViewModels/          MainVM / TestRunVM / ManualVM / ConfigVM / LogVM / DeviceStatusVm
│       ├── Views/               TestRunView / ManualView / ConfigView / LogView + DataMatrixGrid
│       ├── Converters/          BusEventConverters
│       ├── App.xaml / .xaml.cs
│       └── MainWindow.xaml      顶部设备状态栏 + 左侧导航 + 内容区
├── setting/                     运行时配置（自动复制到 bin/setting/）
│   ├── Command.ini              指令字典（多型号 SCPI/串口模板）
│   ├── Setting.ini              设备 SIM/HW 切换 + 端口 + 偏好
│   ├── 工位对应表.csv
│   └── TestConfig/              测试方案目录（一级=方案，二级=传感器型号）
│       └── M30测试/             方案：M30 压力传感器测试
│           ├── Sample.ini
│           ├── M30-HPT-HP-M1.1-M3.0-G20.ini
│           ├── M30-HPT-MP-k100-k400-G20.ini
│           ├── M30-HPT-MP-k47.6-k100-D05.ini
│           ├── M30-HPT-LP-K11.1-K10-D05.ini
│           ├── ...              共 20 个传感器型号
│           └── M10.ini
└── M30TestApp.V2.sln
```

## 内置传感器型号（20 个）

| 型号 | 量程 (kPa) | 压力类型 | 精度 |
| --- | --- | --- | --- |
| Sample | 0 ~ 100 | 表压 | 0.0001 |
| M30-HPT-HP-M1.1-M3.0-G20 | 0 ~ 1100 | 表压 | 0.1 |
| M30-HPT-MP-k100-k400-G20 | 0 ~ 100 | 表压 | 0.005 |
| M30-HPT-MP-k47.6-k100-D05 | 0 ~ 47.6 | 表压 | 0.01 |
| M30-HPT-LP-K11.1-K10-D05 | 0 ~ 10 | 差压 | 0.01 |
| M30-HPT-LP-K4.4-K2.0-D05 | 0 ~ 2 | 差压 | 0.01 |
| M30-HPT-MP-K100-K500-A08 | 5 ~ 100 | 绝压 | 0.01 |
| D2-FS-M30-MP-K35-K100-D05 | 0 ~ 35 | 差压 | 0.01 |
| M30-HPT-MP-k100-k100-G20 | 0 ~ 100 | 表压 | 0.005 |
| M30-HPT-HP-M5.6-M20-G20 | 0 ~ 1100 | 表压 | 0.01 |
| M30-HPT-MP-k189-k500-D05 | 0 ~ 189 | 差压 | 0.1 |
| M30-HPT-MP-K36.1-K130-A08 | 2 ~ 36.1 | 绝压 | 0.01 |
| M30-HPT-MP-K29.4-K25-A08 | 2 ~ 25 | 绝压 | 0.01 |
| M30-HPT-HP-M1.1-M3.0-D05 | 0 ~ 1100 | 差压 | 0.1 |
| M30-HPT-MP-k100-k400-G20-H | 0 ~ 40 | 表压 | 0.1 |
| M10-40K | 0 ~ 40 | 差压 | 0.01 |
| m10-10k | 0 ~ 10 | 差压 | 0.01 |
| m10-2k | 0 ~ 4 | 表压 | 0.1 |
| m10-a100k | 5 ~ 100 | 绝压 | 0.01 |
| M10 | 5 ~ 400 | 绝压 | 0.1 |

每个型号均包含完整的性能指标限值（Offset / Span / Linearity / TCO / TCS / TCR / THO / THS / PressureHysteresis / CT）。

## 运行

```powershell
dotnet build M30TestApp.V2.sln
dotnet run --project src/M30TestApp.Wpf/M30TestApp.Wpf.csproj
```

输出 EXE：`src/M30TestApp.Wpf/bin/Debug/net8.0-windows/M30TestApp.V2.exe`

## 支持设备

| 类型 | 型号 | 协议 |
| --- | --- | --- |
| 压力控制器 | FLUKE-7250 / FLUKE-6270 / WIKA-CPC8000 | GPIB (VISA) |
| 烘箱 | GWSEBWT1670 / GWNMC2000 | RS-232 串口 |
| 数字万用表/切换单元 | Keysight-34970A / Keysight-DAQ973A | GPIB (VISA) |
| 采集卡 | M30-DAC | RS-232 串口 (Modbus) |
| 电源 | ADCMT-6146 | GPIB (VISA) |
| 通道板卡 | Board | RS-232 串口 |

所有设备支持 SIM（模拟）和 HW（真实硬件）两种后端，在 `Setting.ini` 的 `[DefaultLoadClass]` 中切换。

## TaskScript 语法

```
模块:动作[,参数1[,参数2...]]|模块:动作...
```

内置模块：

| 模块 | 动作 | 说明 |
| --- | --- | --- |
| `Initial` | `Pressure` / `Temp` / `Board` / `DMM` / `CommuTest` | 设备初始化 / 通讯自检 |
| `DAQ` | `ClearData` / `TestType,<type>` / `Down` | 清空采集矩阵 / 标记类型 / 下电 |
| `TP` | `SetPressurePoint,<idx>` / `SetTempPoint,<idx>` / `Vent` / `ReturnRoomTemp` / `StopTemp` | 测试点切换（支持表压/绝压/差压自动切换） |
| `Read` | `R` / `UT` / `Usign` / `Usource` / `Isource` / `DMMSample` | 按工位采样并写入矩阵 |
| `Run` | `PerformanceTest` | 完整性能测试流程（温度×压力×工位全自动） |
| `Save` | `TestData` | 导出 CSV |
| `Cal` | `Test` | 指标计算（Offset/Span/NL/TCO/TCS/TCR/THO/THS/PH/TCT） |

## 功能特性

- **实时测试界面**：工位×测试点数据矩阵、合格/警告/异常统计、实时日志
- **手动调试**：压力控制、烘箱温控、阀门控制、单工位/批量采集、SCPI 原始指令
- **方案管理**：方案（一级目录）→ 传感器型号（二级 INI 文件），支持新建/删除/切换
- **配置编辑**：设备参数、指令模板、工位映射、性能指标限值、系统偏好
- **压力类型**：支持表压（Gauge）、绝压（Absolute）、差压（Differential），方案级默认 + 单点覆盖
- **压力值支持小数**：如 47.6 kPa、23.8 kPa、18.05 kPa
- **深色/亮色主题**：设置页切换，启动时恢复
- **断点续测**：异常中断后从上次温度点/压力点继续
- **扫码枪录入**：工位序列号批量扫码
- **最多 256 工位**

## 当前进度

- ✅ WPF MVVM 架构重写（从 WinForms ASLab 迁移）
- ✅ 配置加载层（INI/CSV）
- ✅ 设备抽象层 + SIM 模拟 + HW 真实驱动（VISA/串口）
- ✅ TaskScript 解释器 + 全部内置 Action
- ✅ `Run:PerformanceTest` 完整自动测试流程
- ✅ `DataMatrix` 实时更新 + CSV 导出
- ✅ 指标计算 `MetricsCalculator`（10 项指标）
- ✅ 20 个传感器型号方案（从旧版 SensorProfile.xml 迁移）
- ✅ 方案两级目录结构（方案文件夹 → 传感器型号）
- ✅ 手动调试页（压力/温度/阀门/采集/SCPI）
- ✅ 配置编辑页（方案/指标限值/参数控制/设备/指令/工位/测试流程/系统设置）
- ✅ 深色主题 + 主题持久化
- ✅ 续测/断点恢复
- ✅ 运行前方案确认 + 扫码枪
- ✅ 全局异常捕获（不崩溃）
- ✅ 快速测试（Usig 三压点 + Span/NL 判定）
- ✅ 应用内自动更新（GitHub 优先、Gitee 备用，下载替换重启）
- ✅ Git 双远程镜像（GitHub + Gitee）

## 源码仓库

- GitHub: https://github.com/a534686350/M30TestApp
- Gitee: https://gitee.com/hl515/m30-test-app

## 待办

- [ ] 指令模板编辑写回 `Command.ini`
- [ ] 流程开关绑定到实际测试逻辑
- [ ] HW 真实设备联调测试
