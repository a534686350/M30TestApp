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
├── server/                      多设备总控制台（只读看板，纯标准库 Python + systemd）
│   ├── station_hub.py           收心跳 / 存 SQLite / 渲染看板（无第三方依赖）
│   ├── m30-hub.service          systemd 单元模板
│   ├── deploy.sh                一键部署 / 更新 / 查状态
│   └── README.md
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
- **断点续测**：以**温度点**为单位 —— 程序打开时检测未完成的测试并询问是否继续，
  续测从该温度点的第一个压力点重新测（需重新保温），该温度点旧数据作废、更早温度点数据保留
- **设备后端手动切换**：不自动改后端。系统设置里勾「使用 SIM 模拟设备」= 模拟，不勾 = 真机；
  切换需管理员密码。用 SIM 时数据为模拟值
- **上报设备 ID（站点标识）**：地址/令牌/间隔内置，只需站点 ID（空则自动生成）和昵称；
  默认锁定，输入管理员密码（`123..00`）后显示与修改，附「打开在线工作台」按钮
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

### 2026-10-13

- ✅ 测试矩阵支持手工编辑：序列号可改、单击即进编辑、回车跳到下一个可编辑格
- ✅ 运行方案窗口支持**勾选不连续工位**（如 1-8、10-16），勾选格支持拖动多选、快捷分组自适应步长
- ✅ 顶部新增「工位」页签：独立的**无扫码工位页**（可输入数量、显示表格，三种测试共用）
- ✅ 深色主题全面适配：视图 238 处画刷引用改为动态资源、修好「切了不生效」（ThemeHelper 按文件名匹配字典）、按 WCAG 2.1 提对比度
- ✅ 手动调试页「读取数据」按钮组位置错位修复；通道选择与读取数据合并为一个分类
- ✅ 多设备管理（第一期）：服务器**总控制台只读看板**（`server/`，纯标准库 + systemd 一键部署）
      + 设备端**「站点标识」**（录入唯一 ID / 控制台地址 / 令牌并上报，离线优先不阻塞测试），已端到端跑通
- ✅ **上报设备 ID（站点标识）**改到「配置 → 系统设置」**第一屏**（原来压在页面底部要滚动才看得到），
      并加**管理员密码锁**：默认锁定，要改先输密码（默认 `123..00`，可在 `Setting.ini` 的
      `[Station] AdminPassword` 覆盖）；解锁只在本次运行有效，重启自动上锁
- ✅ 运行方案窗口工位勾选格拖动选择加固：加**鼠标捕获**（拖到格子外面不断线）+ **命中测试**
      （快速从 1 拖到 9 不再漏掉中间几格），拖动中只记账、松手统一重建预览表（原来每勾一格重建一次，卡）
- ✅ **总控制台第二期**：页面改**浅色**（右上角可切深色，localStorage 记住）；设备卡片可**点开看详情**
      （基本信息 / 测试信息 / 工位明细 / 指标判定 / 心跳历史）；**导出数据** —— 工位明细 CSV、
      心跳历史 CSV、完整快照 JSON。上位机上报扩展了型号 / 批次 / 温度点 / 压力点 / 开始时间 /
      已运行 / 工位明细，明细**变化时才发**（指纹去重，256 工位不会每 5 秒重发）
- ✅ **断点续测改成「温度点」粒度**：程序一打开就检测未完成的测试，弹窗问是否继续并让用户
      输入该温度点重新测试前的**保温时长**；续测从该温度点的**第一个压力点重测**，
      该温度点已测数据作废，更早温度点的数据保留（例：停在 T2 的 P3 → 从 T2 的 P0 重来，T1 保留）
- ✅ **修好横向滚动条塌成小点**：主题里的 ScrollBar 样式在基础 setter 里写死了 `Width=10`，
      横向条的"长度"就是它的 Width —— 于是整条被压成 10px 的小疙瘩，看着像"只有中间一个点"、
      还点不着。改成尺寸只按方向设（宽度只在 Vertical 触发器里给、高度只在 Horizontal 里给），
      顶部同步滚动条与表格底部滚动条同时恢复正常
- ✅ 测试完成弹**明显的完成窗**：测试方案 / 传感器型号 / **启动时间** / **完成时间** /
      **测试用时** / 数据目录 + 工位统计（合格·警告·异常），带「打开数据目录」按钮
- ✅ **设备后端改为手动切换**：取消硬件失败自动转 SIM（防止现场以为测了真机其实在跑模拟）。
      系统设置里用「使用 SIM 模拟设备」勾选框手动选择，**需管理员密码解锁**；真实设备连接失败直接报错
      （例如 VISA `HRESULT = 80040012` = 找不到 VISA 资源，即没装 VISA 运行时 / GPIB 卡 / 地址写错）
- ✅ **上报设置不再手填**：控制台地址、令牌、上报间隔全部内置于程序；界面只留**站点 ID**（空则按电脑自动
      生成稳定 ID）和**显示名称**（昵称，可改），并提供「打开在线工作台」按钮直接跳转总控制台；
      新装设备默认启用上报
- ✅ **「方案」提到顶部一级导航**：方案编辑（温度点 / 压力点 / 判定限值）现场用得最多，
      从配置中心里提出来，与「自动测试 / 长期稳定性测试 / 快速测试 / 手动调试 / 工位 / 日志 / 配置」
      并列在顶部页签条最左侧；高亮互斥——在配置中心切到别的节（如系统设置）时高亮自动还给「配置」
- ✅ **工位页三处修复（v1.2.43）**：「选择运行方案 → 工位」的**起始工位 / 所需工位不再只能填 1**
      （数字框逐字符回写，输入中间态被上限 clamp 夹成 256，连带把工位数量压成 1、勾选格只剩 1 个）；
      **起始板卡 / 起始板卡工位**手填值不再被起始工位推导覆盖；「选择测试工位」新增**「全不选」**；
      「起始阀位」改为只读并标注「按工位自动」（阀门恒按工位号推导，原输入框改了不生效）
- ✅ **v1.2.43 已发版**（2026-10-16）：Gitee Release 已出（含自包含 zip），
      总控制台下载页 http://54.206.27.158/download 已列出 v1.2.43 与历史版本

### 常见错误

| 报错 | 含义 / 处理 |
| --- | --- |
| `HRESULT = 80040012` | VISA 找不到资源：GPIB/VISA 驱动没装、仪器没上电、地址写错。看**顶栏设备模式徽标**是否显示「真实设备」，是则改成 SIM 或在配置里接好真机 |
| `RunLongTermStabilityTest: HRESULT = 80040012` | 同上，发生在长期稳定性测试设定压力点时 |

> **设备模式徽标**：标题栏右侧显示 `SIM 模拟`（琥珀色）或 `真实设备`（绿色），一眼可辨。
> 换模式走「配置 → 系统设置 → 行为 → 使用 SIM 模拟设备」，不需要密码。

## 源码仓库

- GitHub: https://github.com/a534686350/M30TestApp
- Gitee: https://gitee.com/hl515/m30-test-app

## 在线地址

- 总控制台：http://54.206.27.158/  （登录 `admin` / `123..00`，已改为自带登录页）
- **程序下载页**：http://54.206.27.158/download  —— 列出全部版本，倒序排列，最新版标「最新」，
  点「下载」直接拿自包含 zip。加历史版本：把 zip 丢进 `artifacts/release/` 后跑 `bash server/deploy.sh`
- 域名 `m30.hl888.cn`：**待加 DNS A 记录指向 `54.206.27.158`**（服务端已就绪、不校验 Host，
  加完即可直接访问）。目前该域名在 Cloudflare 且指向 CF 的 CDN IP，还没指向本服务器。
  步骤见 `server/README.md` 的「绑定域名」章节。

## 待办

- [ ] 指令模板编辑写回 `Command.ini`
- [ ] 流程开关绑定到实际测试逻辑
- [ ] HW 真实设备联调测试
- [ ] 总控制台：加**设备删除**接口 + 页面按钮（当前清下线设备只能 SSH 进服务器改 SQLite）
- [ ] 总控制台第二期：历史数据 / 报表归档（远程控制需另立「指令下发 → 上位机本地安全校验 → 回执」）
- [ ] gitee 检查更新偶发 `403 Forbidden`（限流）——现场只有 gitee 能通时会「检查更新失败」
- [x] ~~发布 v1.2.42：打 tag → 出包 → 建 Gitee/GitHub Release 并上传 zip~~
      Gitee 已完成；**GitHub 未发**（本机网络到 github.com 被代理拒 502、SSH 无密钥），
      网络恢复后补：`git push origin main --follow-tags` + 设 `GH_TOKEN` 跑
      `build/publish-release.ps1 -SkipBuild`（脚本已改为单渠道失败不阻断另一渠道）
- [x] ~~发布 v1.2.43：工位页修复~~（tag + Gitee Release + GitHub Release + 下载页）
- [ ] **GitHub 补推 + tag 修正**：v1.2.43 的 GitHub Release 已发出、zip 正确，但本机
      **git 协议到 github.com 被代理拒 502**（只有 `api.github.com` 通），`main` 与 tag 都没推上去。
      后果：GitHub 上的 `v1.2.43` tag 被 Release API 按**旧 HEAD `c571841`** 建了，指向的不是本次 commit。
      网络恢复后补（顺序不能颠倒）：
      ```
      git push origin main
      git push origin :refs/tags/v1.2.43     # 删掉 API 自动建的那个
      git push origin v1.2.43                # 重新推本地 annotated tag（→ 7f917a8）
      ```
      v1.2.42 及更早的 GitHub 补推同样待办（见上一条）。
