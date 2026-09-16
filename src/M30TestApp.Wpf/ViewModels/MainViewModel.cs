using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
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
    public RelayCommand ShowLogCommand      { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand OpenDataDirCommand  { get; }
    public RelayCommand OpenLogDirCommand   { get; }
    public RelayCommand OpenTestConfigDirCommand { get; }
    public RelayCommand ExitCommand         { get; }
    public RelayCommand ShowDarkThemeCommand { get; }
    public RelayCommand ShowLightThemeCommand { get; }
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
    private string _selectedNavKey = "Test";
    public string SelectedNavKey
    {
        get => _selectedNavKey;
        set
        {
            if (!SetField(ref _selectedNavKey, value)) return;
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageGroupTitle));
            OnPropertyChanged(nameof(CurrentRunStatus));
            OnPropertyChanged(nameof(CurrentRunStep));
            StartSelectedRunCommand?.RaiseCanExecuteChanged();
            StopSelectedRunCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>页面头显示的当前页名。</summary>
    public string PageTitle => SelectedNavKey switch
    {
        "Test" => "测试",
        "Manual" => "手动调试",
        "Plan" => "测试方案配置",
        "Slots" => "工位配置",
        "Config" => "系统配置",
        "Log" => "运行日志",
        "Settings" => "系统设置",
        _ => "自动测试",
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
        Config = new ConfigViewModel(session);
        Log = new LogViewModel();
        Settings = new SettingsViewModel(session);

        // 「设置 → 版本信息」子页要用检查更新 / 回退命令（都住在 SettingsViewModel 里）
        Config.Settings = Settings;

        _currentView = TestRun;

        TestRun.PropertyChanged += OnRunPagePropertyChanged;
        LongTermStability.PropertyChanged += OnRunPagePropertyChanged;
        QuickTest.PropertyChanged += OnQuickTestPropertyChanged;

        // 命令条的开始/停止按当前测试类型分发：自动测试 / 长期稳定性走 TestRunViewModel，
        // 快速测试走 QuickTestViewModel（它页内也自带开始/停止按钮）。
        StartSelectedRunCommand = new RelayCommand(_ => StartActiveTest(), _ => CanStartActiveTest());
        StopSelectedRunCommand = new RelayCommand(_ => StopActiveTest(), _ => CanStopActiveTest());

        // 「测试」主页签 + 命令条上的模式选择器：三种测试共用一个页面。
        // 页签只导航（保留当前模式），模式选择器才改 TestMode。
        ShowTestCenterCommand = new RelayCommand(_ => { CurrentView = TestModeView; SelectedNavKey = "Test"; });
        ShowTestRunCommand  = new RelayCommand(_ => { TestMode = "Auto"; CurrentView = TestModeView; SelectedNavKey = "Test"; });
        ShowLongTermStabilityCommand = new RelayCommand(_ => { TestMode = "LongTerm"; CurrentView = TestModeView; SelectedNavKey = "Test"; });
        ShowQuickTestCommand = new RelayCommand(_ => { TestMode = "Quick"; CurrentView = TestModeView; SelectedNavKey = "Test"; });
        ShowManualCommand   = new RelayCommand(_ => { CurrentView = Manual; SelectedNavKey = "Manual"; });
        // 「设置」主页签 = 全部配置子页的容器（方案/接口设置/温度采集/气路与参数/压力指令/设备/指令/工位/测试流程/版本信息/系统设置）
        ShowConfigCommand   = new RelayCommand(_ => { CurrentView = Config; SelectedNavKey = "Settings"; });
        ShowConfigPlanCommand = new RelayCommand(_ => { Config.SelectedSection = "方案"; CurrentView = Config; SelectedNavKey = "Settings"; });
        ShowConfigSlotsCommand = new RelayCommand(_ => { Config.SelectedSection = "工位"; CurrentView = Config; SelectedNavKey = "Settings"; });
        ShowLogCommand      = new RelayCommand(_ => { CurrentView = Log; SelectedNavKey = "Log"; });
        // 偏好设置（语言/主题/更新）保留独立页，从「系统」菜单进入
        ShowSettingsCommand = new RelayCommand(_ => { OpenSettings(); SelectedNavKey = "Settings"; });

        OpenDataDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.DataDir));
        OpenLogDirCommand  = new RelayCommand(_ => OpenDirectory(AppPaths.LogDir));
        OpenTestConfigDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.TestConfigDir));
        ExitCommand        = new RelayCommand(_ => System.Windows.Application.Current.MainWindow?.Close());
        ShowDarkThemeCommand  = new RelayCommand(_ => Config.SelectedTheme = ThemeHelper.ToDisplayName(ThemeHelper.Dark));
        ShowLightThemeCommand = new RelayCommand(_ => Config.SelectedTheme = ThemeHelper.ToDisplayName(ThemeHelper.Light));
        ShowVersionInfoCommand = new RelayCommand(_ =>
        {
            Config.SelectedSection = "版本信息";
            CurrentView = Config;
            SelectedNavKey = "Settings";
        });

        session.Reconfigured += OnSessionReconfigured;
        session.DevicesRebuilt += OnSessionDevicesRebuilt;

        // 启动后异步检查更新（不阻塞主界面）；发现新版时弹窗提示，不强制安装
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
        }));
    }

    private void OnRunPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestRunViewModel.Status))
            OnPropertyChanged(nameof(CurrentRunStatus));
        if (e.PropertyName == nameof(TestRunViewModel.CurrentStep))
            OnPropertyChanged(nameof(CurrentRunStep));
    }

    private void OnQuickTestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickTestViewModel.Status)) return;
        OnPropertyChanged(nameof(CurrentRunStatus));
        OnPropertyChanged(nameof(CurrentRunStep));
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
        if (TestMode == "Quick")
        {
            if (QuickTest.StartCommand.CanExecute(null)) QuickTest.StartCommand.Execute(null);
            return;
        }

        if (ActiveRunVm.RunCommand.CanExecute(null)) ActiveRunVm.RunCommand.Execute(null);
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

    public void Dispose()
    {
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
}
