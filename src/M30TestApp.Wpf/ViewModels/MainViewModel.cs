using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Net;
using M30TestApp.Core.Devices;
using M30TestApp.Wpf.Mvvm;
using M30TestApp.Wpf.Themes;

namespace M30TestApp.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    public TestSession Session { get; }

    public ObservableCollection<DeviceStatusVm> Devices { get; } = new();

    public TestRunViewModel TestRun { get; }
    public TestRunViewModel LongTermStability { get; }
    public ManualViewModel Manual { get; }
    public QuickTestViewModel QuickTest { get; }
    public ConfigViewModel Config { get; }
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>顶部「工位」页签的页面 VM（无扫码的简化工位页，与配置页共用同一份工位数据）。</summary>
    public SlotsViewModel Slots { get; }

    /// <summary>站点状态上报器（上报给总控制台；只读，不接收控制指令）。</summary>
    public StationReporter Reporter { get; }

    /// <summary>
    /// 是否有测试任务正在运行（自动/长期/快速）。运行中锁定主界面所有导航与配置修改，
    /// 防止操作员切到手动调试或改配置，导致硬件被两套流程同时占用。
    /// </summary>
    public bool IsTaskRunning =>
        TestRun.IsRunning || LongTermStability.IsRunning || QuickTest.IsRunning;

    /// <summary>任务独立窗口是否打开（一次只能一个）。</summary>
    public bool IsTaskWindowOpen => Views.TaskWindow.IsOpen;

    private object _currentView;
    public object CurrentView
    {
        get => _currentView;
        set
        {
            if (!SetField(ref _currentView, value)) return;
            OnPropertyChanged(nameof(CurrentRunStatus));
            OnPropertyChanged(nameof(CurrentRunStep));
            OnPropertyChanged(nameof(CurrentRunModeTitle));
            StartSelectedRunCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 「测试」页里选中的测试类型：Auto / LongTerm / Quick。
    /// 三种测试都是"表格界面"，共用同一个主页签，靠这里的模式切换决定显示哪个子视图。
    /// </summary>
    private string _testMode = "Auto";
    public string TestMode
    {
        get => _testMode;
        set
        {
            if (!SetField(ref _testMode, value)) return;
            OnPropertyChanged(nameof(TestModeView));
            OnPropertyChanged(nameof(SelectedTabKey));
            NotifyTabFlags();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(CurrentRunModeTitle));
            OnPropertyChanged(nameof(CurrentRunStatus));
            OnPropertyChanged(nameof(CurrentRunStep));
            if (SelectedNavKey == "Test") CurrentView = TestModeView;
            StartSelectedRunCommand?.RaiseCanExecuteChanged();
            StopSelectedRunCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>「测试」页当前应显示的子视图。</summary>
    public object TestModeView => TestMode switch
    {
        "LongTerm" => LongTermStability,
        "Quick" => QuickTest,
        _ => TestRun,
    };

    private TestRunViewModel ActiveRunVm => TestMode == "LongTerm" ? LongTermStability : TestRun;

    public string CurrentRunStatus => TestMode == "Quick" ? QuickTest.Status : ActiveRunVm.Status;
    public string CurrentRunStep => TestMode == "Quick" ? QuickTest.Status : ActiveRunVm.CurrentStep;
    public string CurrentRunModeTitle => TestMode switch
    {
        "LongTerm" => "长期稳定性测试",
        "Quick" => "快速测试",
        _ => "自动测试",
    };

    /// <summary>只切换到「测试」页，不改动已选测试类型（页签用）。</summary>
    public RelayCommand ShowTestCenterCommand { get; }
    public RelayCommand ShowTestRunCommand  { get; }
    public RelayCommand ShowLongTermStabilityCommand { get; }
    public RelayCommand StartSelectedRunCommand { get; }
    public RelayCommand StopSelectedRunCommand { get; }
    public RelayCommand ShowManualCommand   { get; }
    public RelayCommand ShowQuickTestCommand { get; }
    public RelayCommand ShowConfigCommand   { get; }
    public RelayCommand ShowConfigPlanCommand { get; }
    public RelayCommand ShowConfigSlotsCommand { get; }

    /// <summary>顶部「工位」页签：一步进到配置页的工位节（工位表 + 测试范围 + 扫码录入）。</summary>
    public RelayCommand ShowSlotsCommand { get; }
    public RelayCommand ShowLogCommand      { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenDataDirCommand  { get; }
    public RelayCommand OpenLogDirCommand   { get; }
    public RelayCommand OpenTestConfigDirCommand { get; }
    public RelayCommand ExitCommand         { get; }
    public RelayCommand ShowDarkThemeCommand { get; }
    public RelayCommand ShowLightThemeCommand { get; }

    /// <summary>顶栏一键切主题（深色 ⇄ 浅色）。</summary>
    public RelayCommand ToggleThemeCommand { get; }

    /// <summary>主题按钮上的文字 = 点一下会切到哪个主题。</summary>
    public string ToggleThemeText =>
        ThemeHelper.Normalize(AppPreferences.Theme(Session.Context.Settings)) == ThemeHelper.Dark ? "浅色" : "深色";
    public RelayCommand ShowVersionInfoCommand { get; }

    /// <summary>标题栏/状态栏显示的程序版本号。</summary>
    public string VersionText
    {
        get
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "V1.0" : $"V{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public string StationTitle { get; }
    public string PlanTitle => $"测试方案 · {Session.Plan.Name}";

    /// <summary>
    /// 顶部模块页签的选中态。与 XAML 里每个页签的 Tag 对应，用 NavKeyIsConverter 双向绑定。
    /// 只影响高亮与页面头文案，不参与任何业务流程。
    /// </summary>
    // 启动落在工位表（测试任务是独立窗口，主窗不再默认停在测试页）
    private string _selectedNavKey = "Slots";
    public string SelectedNavKey
    {
        get => _selectedNavKey;
        set
        {
            if (!SetField(ref _selectedNavKey, value)) return;
            OnPropertyChanged(nameof(SelectedTabKey));
            NotifyTabFlags();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(PageGroupTitle));
            OnPropertyChanged(nameof(IsTestFamily));
            OnPropertyChanged(nameof(CurrentRunStatus));
            OnPropertyChanged(nameof(CurrentRunStep));
            StartSelectedRunCommand?.RaiseCanExecuteChanged();
            StopSelectedRunCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 顶部页签的高亮键。测试族三页各占一个（Auto / LongTerm / Quick），其余模块沿用
    /// <see cref="SelectedNavKey"/>。
    /// 必须单独一个属性：三种测试都是 <see cref="SelectedNavKey"/> == "Test"，
    /// 光看它分不出当前是哪一个测试页。
    /// </summary>
    public string SelectedTabKey => SelectedNavKey == "Test"
        ? TestMode switch { "LongTerm" => "LongTerm", "Quick" => "Quick", _ => "Auto" }
        : SelectedNavKey;

    // 页签选中态用 6 个显式 bool，而不是「SelectedTabKey + ConverterParameter」转换器。
    // 转换器 + RadioButton.IsChecked 这套组合出过一次「点了长期稳定性、高亮却停在手动调试」，
    // 改成显式属性后每次状态变化都明确通知，不再依赖转换器与 GroupName 的交互。
    public bool IsTabAuto => SelectedTabKey == "Auto";
    public bool IsTabLongTerm => SelectedTabKey == "LongTerm";
    public bool IsTabQuick => SelectedTabKey == "Quick";
    public bool IsTabManual => SelectedNavKey == "Manual";
    public bool IsTabSlots => SelectedNavKey == "Slots";
    public bool IsTabLog => SelectedNavKey == "Log";
    public bool IsTabSettings => SelectedNavKey == "Settings"
        && !string.Equals(Config.SelectedSection, "方案", StringComparison.Ordinal);

    /// <summary>
    /// 顶部「方案」页签：现场用得最多，从配置中心里提到一级导航。
    /// 高亮条件 = 在配置中心且正好停在方案编辑节（在配置中心切到别的节就还给「配置」）。
    /// </summary>
    public bool IsTabPlan => SelectedNavKey == "Settings"
        && string.Equals(Config.SelectedSection, "方案", StringComparison.Ordinal);

    /// <summary>页签选中态一次性通知（每次导航 / 模式变化都要刷）。</summary>
    private void NotifyTabFlags()
    {
        OnPropertyChanged(nameof(IsTabAuto));
        OnPropertyChanged(nameof(IsTabLongTerm));
        OnPropertyChanged(nameof(IsTabQuick));
        OnPropertyChanged(nameof(IsTabManual));
        OnPropertyChanged(nameof(IsTabSlots));
        OnPropertyChanged(nameof(IsTabLog));
        OnPropertyChanged(nameof(IsTabSettings));
        OnPropertyChanged(nameof(IsTabPlan));
    }

    /// <summary>是否停在测试族三页之一（决定命令条显示「开始/停止」还是页面标题）。</summary>
    public bool IsTestFamily => SelectedNavKey == "Test";

    /// <summary>页面头显示的当前页名（测试族显示具体模式名，不再笼统叫「测试」）。</summary>
    public string PageTitle => SelectedNavKey switch
    {
        "Test" => CurrentRunModeTitle,
        "Manual" => "手动调试",
        "Log" => "运行日志",
        "Settings" => "配置中心",
        "Plan" => "测试方案配置",
        "Slots" => "工位配置",
        _ => "自动测试",
    };

    /// <summary>页面头副标题：非测试族页签说明这一页是干什么的；测试族为空（命令条显示工步）。</summary>
    public string PageSubtitle => SelectedNavKey switch
    {
        "Manual" => "设备点动 · 单次读数 · 状态刷新",
        "Slots" => "输入工位数量生成工位表；扫码录入请到「配置 → 工位」",
        "Log" => "全量运行日志 · 关键字过滤",
        "Settings" => "方案 / 工位 / 设备 / 指令",
        _ => "",
    };

    /// <summary>页面头显示的分组名（面包屑上一级）。</summary>
    public string PageGroupTitle => SelectedNavKey is "Test" or "Manual"
        ? "测试执行"
        : "数据与配置";

    public MainViewModel(TestSession session, string stationTitle = "M30测试专用")
    {
        Session = session;
        StationTitle = stationTitle;

        Devices.Add(new DeviceStatusVm("压控", session.Pressure));
        var ovenStatus = new DeviceStatusVm("烘箱", session.Oven);
        Devices.Add(ovenStatus);
        Devices.Add(new DeviceStatusVm("切换单元",  session.Dmm));
        var dacStatus = new DeviceStatusVm("板卡", session.Dac);
        Devices.Add(dacStatus);
        Devices.Add(new DeviceStatusVm("通道板", session.Board));

        TestRun = new TestRunViewModel(session);
        LongTermStability = new TestRunViewModel(session, isLongTermStabilityMode: true);
        Manual = new ManualViewModel(session, ovenStatus, dacStatus);
        QuickTest = new QuickTestViewModel(session);
        // 设备配置编辑器只创建一份，配置页与偏好设置页共用 —— 两页各 new 一个的话，
        // 在设置页改了地址，配置页的表格还是旧值。
        var deviceConfig = new DeviceConfigSet();
        Config = new ConfigViewModel(session, deviceConfig);
        // 顶部「工位」页签：无扫码的简化页。**只是 Config 的一层包装**，不复制工位数据，
        // 保证工位表只有一份（配置页 / 工位页 / 三种测试共用）。
        Slots = new SlotsViewModel(Config);
        Log = new LogViewModel();
        Config.PropertyChanged += OnConfigPropertyChanged;
        Settings = new SettingsViewModel(session, deviceConfig);

        // 「设置 → 版本信息」子页要用检查更新 / 回退命令（都住在 SettingsViewModel 里）
        Config.Settings = Settings;

        // 站点状态上报（总控制台）。**只上报、不接收任何控制指令**；连不上只是重试，
        // 绝不阻塞测试。设置页改完「站点标识」会调 Reporter.Apply 立即生效。
        Reporter = new StationReporter(BuildStationSnapshot);
        Settings.Reporter = Reporter;
        var stationConfig = StationConfig.Load(session.Context.Settings);
        // 没填过 ID 时自动落盘一个稳定的机器 ID；不要求现场先解锁设置页，
        // 只要已经配置了控制台地址并启用上报，后台就能直接上报。
        if (string.IsNullOrWhiteSpace(session.Context.Settings.Get(StationConfig.Section, "Id", "")))
        {
            stationConfig.SaveTo(session.Context.Settings);
            try { session.Context.Settings.Save(AppPaths.SettingIni); }
            catch (Exception ex) { AppLog.Warn("Station", "自动保存设备 ID 失败：" + ex.Message); }
        }
        Reporter.Apply(stationConfig);

        // 启动落在「工位表」主界面：测试任务已改成独立窗口（点左侧导航才开），
        // 不能再默认显示自动测试页 —— 那样主窗里既是任务页又是主界面，语义混乱。
        _currentView = Slots;

        TestRun.PropertyChanged += OnRunPagePropertyChanged;
        LongTermStability.PropertyChanged += OnRunPagePropertyChanged;
        QuickTest.PropertyChanged += OnQuickTestPropertyChanged;

        // 命令条的开始/停止按当前测试类型分发：自动测试 / 长期稳定性走 TestRunViewModel，
        // 快速测试走 QuickTestViewModel（它页内也自带开始/停止按钮）。
        StartSelectedRunCommand = new RelayCommand(_ => StartActiveTest(), _ => CanStartActiveTest());
        StopSelectedRunCommand = new RelayCommand(_ => StopActiveTest(), _ => CanStopActiveTest());

        // 「测试」主页签 + 命令条上的模式选择器：三种测试共用一个页面。
        // 页签只导航（保留当前模式），模式选择器才改 TestMode。
        // 测试任务改成独立窗口：一次只开一个，关掉回主界面。
        // 运行中（IsTaskRunning）禁止开新任务窗，也禁止切到手动调试 / 改配置。
        ShowTestCenterCommand = new RelayCommand(_ => OpenTaskWindow("Test", "测试中心", ActiveRunVm),
            _ => !IsTaskRunning);
        ShowTestRunCommand  = new RelayCommand(_ =>
        {
            TestMode = "Auto";
            OpenTaskWindow("Auto", "自动测试", TestRun);
        }, _ => !IsTaskRunning);
        ShowLongTermStabilityCommand = new RelayCommand(_ =>
        {
            TestMode = "LongTerm";
            OpenTaskWindow("LongTerm", "长期稳定性测试", LongTermStability);
        }, _ => !IsTaskRunning);
        ShowQuickTestCommand = new RelayCommand(_ =>
        {
            TestMode = "Quick";
            OpenTaskWindow("Quick", "快速测试", QuickTest);
        }, _ => !IsTaskRunning);
        ShowManualCommand   = new RelayCommand(_ => OpenTaskWindow("Manual", "手动调试", Manual),
            _ => !IsTaskRunning);
        // 「设置」主页签 = 全部配置子页的容器；命令定义在下面（带运行中锁定）。
        // 顶部「工位」页签 → 独立的简化工位页（无扫码录入）：只输入工位数量、看工位表。
        // 系统菜单的「工位配置」→ 配置页的工位节（带扫码录入），供现场扫码场景使用。
        // 两者共用同一份工位数据（Slots 页只是 Config 的包装）。
        ShowSlotsCommand = new RelayCommand(_ =>
        {
            CurrentView = Slots;
            SelectedNavKey = "Slots";
        }, _ => !IsTaskRunning);
        ShowConfigSlotsCommand = new RelayCommand(_ =>
        {
            Config.SelectedSection = "工位";
            CurrentView = Config;
            SelectedNavKey = "Settings";
        }, _ => !IsTaskRunning);
        ShowLogCommand      = new RelayCommand(_ => { CurrentView = Log; SelectedNavKey = "Log"; },
            _ => !IsTaskRunning);
        // 偏好设置（语言/主题/更新）保留独立页，从「系统」菜单进入
        ShowSettingsCommand = new RelayCommand(_ => { OpenSettings(); SelectedNavKey = "Settings"; },
            _ => !IsTaskRunning);
        // 配置页也锁定（运行中禁止改配置）
        ShowConfigCommand = new RelayCommand(_ => { CurrentView = Config; SelectedNavKey = "Settings"; },
            _ => !IsTaskRunning);
        ShowConfigPlanCommand = new RelayCommand(_ => { Config.SelectedSection = "方案"; CurrentView = Config; SelectedNavKey = "Settings"; },
            _ => !IsTaskRunning);

        OpenDataDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.DataDir));
        OpenLogDirCommand  = new RelayCommand(_ => OpenDirectory(AppPaths.LogDir));
        OpenTestConfigDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.TestConfigDir));
        ExitCommand        = new RelayCommand(_ => System.Windows.Application.Current.MainWindow?.Close());
        ShowDarkThemeCommand  = new RelayCommand(_ => Config.SelectedTheme = ThemeHelper.ToDisplayName(ThemeHelper.Dark));
        ShowLightThemeCommand = new RelayCommand(_ => Config.SelectedTheme = ThemeHelper.ToDisplayName(ThemeHelper.Light));
        ToggleThemeCommand = new RelayCommand(_ =>
        {
            // 顶栏一键切主题：原来藏在「系统」菜单第三层，现场要点三次才换得到。
            var cur = ThemeHelper.Normalize(AppPreferences.Theme(Session.Context.Settings));
            var next = cur == ThemeHelper.Dark ? ThemeHelper.Light : ThemeHelper.Dark;
            AppPreferences.Set(Session.Context.Settings, "Theme", next);
            try { Session.Context.Settings.Save(AppPaths.SettingIni); } catch { }
            Config.SelectedTheme = ThemeHelper.ToDisplayName(next);   // 同步配置页下拉，避免两处显示不一致
            OnPropertyChanged(nameof(ToggleThemeText));
            AppLog.Info("Ui", $"主题切换为 {next}");
        });
        ShowVersionInfoCommand = new RelayCommand(_ =>
        {
            Config.SelectedSection = "版本信息";
            CurrentView = Config;
            SelectedNavKey = "Settings";
        });

        session.Reconfigured += OnSessionReconfigured;
        session.DevicesRebuilt += OnSessionDevicesRebuilt;

        // 启动后异步检查更新（不阻塞主界面）；发现新版时弹窗提示，不强制安装。
        // 截图自检模式（--shot-*）跳过：模态更新框会阻塞 dispatcher，导致截图定时器永不触发、进程挂住。
        var isShotMode = Environment.GetCommandLineArgs()
            .Any(a => a.StartsWith("--shot-", StringComparison.OrdinalIgnoreCase));
        if (!isShotMode)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(3000);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _ = Settings.CheckForUpdateOnStartupAsync();
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Startup", $"检查更新失败: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// 程序打开时检测「未完成的测试」并询问是否继续（由 App 在窗口显示后延迟调用）。
    ///
    /// 断点粒度是**温度点**：续测从该温度点的第一个压力点重测，该温度点已测的数据作废，
    /// 更早温度点的数据保留。重新测试前的保温时长由用户在弹窗里输入。
    ///
    /// 三个分支：
    ///   继续测试 → 记住保温时长，切到测试页并走正常的开始流程（会弹运行方案窗口确认方案/工位）
    ///   重新开始 → 删掉断点，什么都不启动
    ///   取消     → 断点原样保留，本次不动作（用户之后点开始测试还会再问）
    /// </summary>
    public void PromptPendingResumeOnStartup()
    {
        try
        {
            if (!AppPreferences.SaveCheckpointOnAbort(Session.Context.Settings)) return;
            if (!TestCheckpoint.Exists()) return;

            var ck = TestCheckpoint.Load();
            if (ck is null) return;

            var answer = TestRunViewModel.ShowResumeCheckpointDialog(ck);

            if (answer.Cancel)
            {
                AppLog.Info("Run", "启动检测到未完成测试：用户选择取消，断点保留");
                return;
            }

            if (answer.Resume == false)
            {
                TestCheckpoint.Delete();
                AppLog.Info("Run", "启动检测到未完成测试：用户选择重新开始，断点已清除");
                return;
            }

            var run = ActiveRunVm;
            run.PendingResumeSoakMinutes = answer.ResumeSoakMinutes;
            run.SkipResumePrompt = true;          // 刚才已经问过了，别在开始流程里再问一遍

            TestMode = "Auto";
            CurrentView = TestModeView;
            SelectedNavKey = "Test";
            AppLog.Info("Run",
                $"启动续测：温度点 #{ck.TempIndex + 1} 重新开始，保温 {answer.ResumeSoakMinutes ?? 0} 分钟");
            StartActiveTest();
        }
        catch (Exception ex)
        {
            // 续测检测失败绝不能挡住程序启动
            AppLog.Warn("Run", "启动续测检测失败：" + ex.Message);
        }
    }

    private void OnSessionReconfigured(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            OnPropertyChanged(nameof(PlanTitle))));
    }

    private void OnSessionDevicesRebuilt(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Devices.Count < 5) return;
            Devices[0].SetDevice(Session.Pressure);
            Devices[1].SetDevice(Session.Oven);
            Devices[2].SetDevice(Session.Dmm);
            Devices[3].SetDevice(Session.Dac);
            Devices[4].SetDevice(Session.Board);

            OnPropertyChanged(nameof(DeviceModeText));
            OnPropertyChanged(nameof(DeviceModeLed));
        }));
    }

    /// <summary>当前设备后端：真机 / SIM 模拟。顶栏徽标让现场一眼看出在哪个模式。</summary>
    public bool IsSimulationMode => Session.DebugMode;

    public string DeviceModeText => Session.DebugMode ? "SIM 模拟" : "真实设备";

    /// <summary>徽标小灯颜色：SIM 用琥珀色提醒，真机用绿色（真机才是正式测）。</summary>
    public System.Windows.Media.Brush DeviceModeLed
    {
        get
        {
            var key = Session.DebugMode ? "LedWarnBrush" : "LedOnBrush";
            return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Gray;
        }
    }

    private void OnRunPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestRunViewModel.Status))
            OnPropertyChanged(nameof(CurrentRunStatus));
        if (e.PropertyName == nameof(TestRunViewModel.CurrentStep))
            OnPropertyChanged(nameof(CurrentRunStep));
        if (e.PropertyName == nameof(TestRunViewModel.IsRunning))
            NotifyRunLockChanged();
    }

    private void OnQuickTestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickTestViewModel.Status))
        {
            OnPropertyChanged(nameof(CurrentRunStatus));
            OnPropertyChanged(nameof(CurrentRunStep));
        }
        if (e.PropertyName == nameof(QuickTestViewModel.IsRunning))
            NotifyRunLockChanged();
    }

    /// <summary>运行态变化：主界面所有导航/配置入口立刻失效（CanExecute 重算）。</summary>
    private void NotifyRunLockChanged()
    {
        OnPropertyChanged(nameof(IsTaskRunning));
        ShowTestCenterCommand.RaiseCanExecuteChanged();
        ShowTestRunCommand.RaiseCanExecuteChanged();
        ShowLongTermStabilityCommand.RaiseCanExecuteChanged();
        ShowQuickTestCommand.RaiseCanExecuteChanged();
        ShowManualCommand.RaiseCanExecuteChanged();
        ShowSlotsCommand.RaiseCanExecuteChanged();
        ShowLogCommand.RaiseCanExecuteChanged();
        ShowConfigCommand.RaiseCanExecuteChanged();
        ShowConfigPlanCommand.RaiseCanExecuteChanged();
        ShowConfigSlotsCommand.RaiseCanExecuteChanged();
        ShowSettingsCommand.RaiseCanExecuteChanged();
        StartSelectedRunCommand.RaiseCanExecuteChanged();
        StopSelectedRunCommand.RaiseCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 打开测试任务独立窗口（自动/长期/快速/手动调试）。
    /// 复用长生命周期任务 VM 单例 —— 绝不 new，否则丢状态且和顶栏命令指向不同实例。
    /// </summary>
    private void OpenTaskWindow(string key, string title, object taskVm)
    {
        if (IsTaskRunning) return;   // 运行中禁止换任务

        // 顶栏开始/停止按 SelectedNavKey + TestMode 分发，任务窗也用同一套。
        // 主界面保持在工位表，不切 CurrentView —— 任务内容只在独立窗口里显示。
        SelectedNavKey = key == "Manual" ? "Manual" : "Test";

        var opened = Views.TaskWindow.Open(key, title, this, taskVm, () =>
        {
            // 关窗回主界面：清掉任务导航态，主界面回到工位表
            SelectedNavKey = "Slots";
            CurrentView = Slots;
            OnPropertyChanged(nameof(IsTaskRunning));
            OnPropertyChanged(nameof(IsTaskWindowOpen));
            CommandManager.InvalidateRequerySuggested();
        });
        if (!opened)
            AppLog.Warn("Shell", $"已有其它测试任务窗口打开，拒绝同时开「{title}」");
        OnPropertyChanged(nameof(IsTaskWindowOpen));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenSettings() => CurrentView = Settings;

    private static void OpenDirectory(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shell", $"打开目录失败 {path}: {ex.Message}");
        }
    }

    // ─── 「测试」页：按 TestMode 分发的启动 / 停止 ──────────────────────────
    private bool CanStartActiveTest() => SelectedNavKey == "Test"
        && (TestMode == "Quick" ? QuickTest.StartCommand.CanExecute(null) : ActiveRunVm.RunCommand.CanExecute(null));

    private void StartActiveTest()
    {
        if (SelectedNavKey != "Test") return;

        // 开始前先做一轮设备连通性预检（压力控制器 / 烘箱 / 本轮用到的 973 / 电路板 / DAC）。
        // 有失败项时窗口会给「仍然开始」选项，不硬拦 —— 现场有时要先跑起来再查线。
        if (!RunPreflight()) return;

        if (TestMode == "Quick")
        {
            if (QuickTest.StartCommand.CanExecute(null)) QuickTest.StartCommand.Execute(null);
            return;
        }

        if (ActiveRunVm.RunCommand.CanExecute(null)) ActiveRunVm.RunCommand.Execute(null);
    }

    /// <summary>弹设备预检窗口；返回 true 表示用户确认继续。</summary>
    private bool RunPreflight()
    {
        try
        {
            var owner = System.Windows.Application.Current.Windows
                .OfType<System.Windows.Window>()
                .FirstOrDefault(w => w.IsActive)
                ?? System.Windows.Application.Current.MainWindow;

            var win = new Views.PreflightWindow(Session, Session.Context.Settings) { Owner = owner };
            var result = win.ShowDialog();
            return result == true && win.Proceed;
        }
        catch (Exception ex)
        {
            // 预检窗口自身出问题不能挡住开工
            AppLog.Warn("Run", $"设备预检窗口异常，跳过预检直接开始：{ex.Message}");
            return true;
        }
    }

    private bool CanStopActiveTest() => SelectedNavKey == "Test"
        && (TestMode == "Quick" ? QuickTest.StopCommand.CanExecute(null) : ActiveRunVm.CancelCommand.CanExecute(null));

    private void StopActiveTest()
    {
        if (SelectedNavKey != "Test") return;
        if (TestMode == "Quick")
        {
            if (QuickTest.StopCommand.CanExecute(null)) QuickTest.StopCommand.Execute(null);
            return;
        }

        if (ActiveRunVm.CancelCommand.CanExecute(null)) ActiveRunVm.CancelCommand.Execute(null);
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigViewModel.SelectedSection))
        {
            OnPropertyChanged(nameof(IsTabPlan));
            OnPropertyChanged(nameof(IsTabSettings));
        }
    }

    public void Dispose()
    {
        Config.PropertyChanged -= OnConfigPropertyChanged;
        Session.Reconfigured -= OnSessionReconfigured;
        Session.DevicesRebuilt -= OnSessionDevicesRebuilt;
        TestRun.PropertyChanged -= OnRunPagePropertyChanged;
        LongTermStability.PropertyChanged -= OnRunPagePropertyChanged;
        QuickTest.PropertyChanged -= OnQuickTestPropertyChanged;
        TestRun.Dispose();
        LongTermStability.Dispose();
        Manual.Dispose();
        QuickTest.Dispose();
        Log.Dispose();
        foreach (var device in Devices)
            device.Dispose();
        Session.Dispose();
    }
    /// <summary>
    /// 采集一份上报给总控制台的状态快照。
    /// 必须**不抛异常**（拿不到就返回空快照）—— 上报失败绝不能影响测试。
    /// </summary>
    private StationSnapshot BuildStationSnapshot()
    {
        string state, step;
        double progress = 0;
        int total = 0, ok = 0, warn = 0, err = 0;
        string startedAt = "", elapsed = "", tempPoint = "", pressurePoint = "";

        var modeNote = TestMode switch
        {
            "LongTerm" => "长期稳定性",
            "Quick" => "快速测试",
            _ => "自动测试",
        };

        if (TestMode == "Quick")
        {
            state = QuickTest.IsRunning ? "running" : "idle";
            step = QuickTest.Status ?? "";
        }
        else
        {
            var run = ActiveRunVm;
            var running = run.IsRunning;
            state = !running ? "idle"
                : (run.Status?.Contains("暂停", StringComparison.Ordinal) == true ? "paused" : "running");
            step = run.CurrentStep ?? "";
            progress = run.ProgressPercentValue / 100.0;
            total = run.TotalSlots;
            ok = (int)run.OkCount;
            warn = (int)run.WarnCount;
            err = (int)run.ErrCount;
            elapsed = run.Elapsed ?? "";
            tempPoint = run.CurrentT ?? "";
            pressurePoint = run.CurrentP ?? "";
            if (run.StartedAt is { } t) startedAt = t.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // 位置参数：State, Plan, Step, Progress, Alarms, SlotTotal, SlotOk, SlotWarn, SlotErr, Note
        return new StationSnapshot(state, Session.Plan.Name ?? "", step, progress,
            err, total, ok, warn, err, modeNote,
            Model: Session.Plan.SensorType ?? "",
            Batch: Session.Context.Settings.Get("Slots", "BatchNo", ""),
            StartedAt: startedAt,
            Elapsed: elapsed,
            TempPoint: tempPoint,
            PressurePoint: pressurePoint,
            Slots: BuildStationSlots());
    }

    /// <summary>
    /// 汇总工位明细（工位号 / 序列号 / 判定）给总控制台。
    ///
    /// 判定取该工位所有采集单元格里最严重的一个：有 Error 记 err、有 Warn 记 warn、
    /// 有数据但全 Ok 记 ok、一格数据都没有记 idle（未测）。
    /// </summary>
    private IReadOnlyList<StationSlotInfo>? BuildStationSlots()
    {
        var run = TestMode == "Quick" ? null : ActiveRunVm;

        if (run is not null && run.Rows.Count > 0)
        {
            var list = new List<StationSlotInfo>(run.Rows.Count);
            foreach (var row in run.Rows)
            {
                var sawData = false;
                var hasWarn = false;
                var hasErr = false;
                foreach (var kv in row.Cells)
                {
                    var status = kv.Value?.Status ?? CellStatus.Empty;
                    if (status == CellStatus.Empty) continue;
                    sawData = true;
                    if (status == CellStatus.Error) hasErr = true;
                    else if (status == CellStatus.Warn) hasWarn = true;
                }

                var state = hasErr ? "err" : hasWarn ? "warn" : sawData ? "ok" : "idle";
                list.Add(new StationSlotInfo(row.Slot, row.SerialNo ?? "", state));
            }
            return list;
        }

        // 还没跑起来 / 快速测试：只报工位表里的序列号，判定留空
        var entries = Session.Slots.Entries;
        if (entries.Count == 0) return null;
        var idle = new List<StationSlotInfo>(entries.Count);
        foreach (var e in entries)
            idle.Add(new StationSlotInfo(e.Slot, e.SerialNo ?? "", "idle"));
        return idle;
    }
}
