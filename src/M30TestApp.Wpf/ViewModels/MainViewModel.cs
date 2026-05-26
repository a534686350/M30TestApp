using System;
using System.Collections.ObjectModel;
using System.IO;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Devices;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public TestSession Session { get; }

    public ObservableCollection<DeviceStatusVm> Devices { get; } = new();

    public TestRunViewModel TestRun { get; }
    public ManualViewModel Manual { get; }
    public ConfigViewModel Config { get; }
    public LogViewModel Log { get; }

    private object _currentView;
    public object CurrentView { get => _currentView; set => SetField(ref _currentView, value); }

    public RelayCommand ShowTestRunCommand { get; }
    public RelayCommand ShowManualCommand  { get; }
    public RelayCommand ShowConfigCommand  { get; }
    public RelayCommand ShowLogCommand     { get; }

    public string StationTitle { get; }
    public string PlanTitle => $"测试方案 · {Session.Plan.Name}";

    public MainViewModel(TestSession session, string stationTitle = "M30 测试上位机")
    {
        Session = session;
        StationTitle = stationTitle;

        Devices.Add(new DeviceStatusVm("压控", session.Pressure));
        var ovenStatus = new DeviceStatusVm("烘箱", session.Oven);
        Devices.Add(ovenStatus);
        Devices.Add(new DeviceStatusVm("DMM",  session.Dmm));
        var dacStatus = new DeviceStatusVm("采集", session.Dac);
        Devices.Add(dacStatus);
        Devices.Add(new DeviceStatusVm("电源", session.Power));
        Devices.Add(new DeviceStatusVm("板卡", session.Board));

        TestRun = new TestRunViewModel(session);
        Manual = new ManualViewModel(session, ovenStatus, dacStatus);
        Config = new ConfigViewModel(session);
        Log = new LogViewModel();

        _currentView = TestRun;

        ShowTestRunCommand = new RelayCommand(_ => CurrentView = TestRun);
        ShowManualCommand  = new RelayCommand(_ => CurrentView = Manual);
        ShowConfigCommand  = new RelayCommand(_ => CurrentView = Config);
        ShowLogCommand     = new RelayCommand(_ => CurrentView = Log);

        session.Reconfigured += (_, _) =>
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                OnPropertyChanged(nameof(PlanTitle))));
        };
    }
}
