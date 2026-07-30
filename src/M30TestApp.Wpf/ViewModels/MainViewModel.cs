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
        }
    }

    public string CurrentRunStatus => CurrentView is TestRunViewModel run ? run.Status : TestRun.Status;
    public string CurrentRunStep => CurrentView is TestRunViewModel run ? run.CurrentStep : TestRun.CurrentStep;

    public RelayCommand ShowTestRunCommand  { get; }
    public RelayCommand ShowLongTermStabilityCommand { get; }
    public RelayCommand ShowManualCommand   { get; }
    public RelayCommand ShowQuickTestCommand { get; }
    public RelayCommand ShowConfigCommand   { get; }
    public RelayCommand ShowConfigPlanCommand { get; }
    public RelayCommand ShowConfigSlotsCommand { get; }
    public RelayCommand ShowLogCommand      { get; }
    public RelayCommand ShowSettingsCommand { get; }

    public string StationTitle { get; }
    public string PlanTitle => $"测试方案 · {Session.Plan.Name}";

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
        Devices.Add(new DeviceStatusVm("电源", session.Power));
        Devices.Add(new DeviceStatusVm("通道板", session.Board));

        TestRun = new TestRunViewModel(session);
        LongTermStability = new TestRunViewModel(session, isLongTermStabilityMode: true);
        Manual = new ManualViewModel(session, ovenStatus, dacStatus);
        QuickTest = new QuickTestViewModel(session);
        Config = new ConfigViewModel(session);
        Log = new LogViewModel();
        Settings = new SettingsViewModel(session);

        _currentView = TestRun;

        TestRun.PropertyChanged += OnRunPagePropertyChanged;
        LongTermStability.PropertyChanged += OnRunPagePropertyChanged;

        ShowTestRunCommand  = new RelayCommand(_ => CurrentView = TestRun);
        ShowLongTermStabilityCommand = new RelayCommand(_ => CurrentView = LongTermStability);
        ShowManualCommand   = new RelayCommand(_ => CurrentView = Manual);
        ShowQuickTestCommand = new RelayCommand(_ => CurrentView = QuickTest);
        ShowConfigCommand   = new RelayCommand(_ => CurrentView = Config);
        ShowConfigPlanCommand = new RelayCommand(_ => { Config.SelectedSection = "方案"; CurrentView = Config; });
        ShowConfigSlotsCommand = new RelayCommand(_ => { Config.SelectedSection = "工位"; CurrentView = Config; });
        ShowLogCommand      = new RelayCommand(_ => CurrentView = Log);
        ShowSettingsCommand = new RelayCommand(_ => OpenSettings());

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
            if (Devices.Count < 6) return;
            Devices[0].SetDevice(Session.Pressure);
            Devices[1].SetDevice(Session.Oven);
            Devices[2].SetDevice(Session.Dmm);
            Devices[3].SetDevice(Session.Dac);
            Devices[4].SetDevice(Session.Power);
            Devices[5].SetDevice(Session.Board);
        }));
    }

    private void OnRunPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != CurrentView) return;
        if (e.PropertyName == nameof(TestRunViewModel.Status))
            OnPropertyChanged(nameof(CurrentRunStatus));
        if (e.PropertyName == nameof(TestRunViewModel.CurrentStep))
            OnPropertyChanged(nameof(CurrentRunStep));
    }

    private void OpenSettings() => CurrentView = Settings;

    public void Dispose()
    {
        Session.Reconfigured -= OnSessionReconfigured;
        Session.DevicesRebuilt -= OnSessionDevicesRebuilt;
        TestRun.PropertyChanged -= OnRunPagePropertyChanged;
        LongTermStability.PropertyChanged -= OnRunPagePropertyChanged;
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
