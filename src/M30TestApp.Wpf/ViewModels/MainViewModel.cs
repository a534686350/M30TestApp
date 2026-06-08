using System;
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
    public ManualViewModel Manual { get; }
    public QuickTestViewModel QuickTest { get; }
    public ConfigViewModel Config { get; }
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }

    private object _currentView;
    public object CurrentView { get => _currentView; set => SetField(ref _currentView, value); }

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
        Manual = new ManualViewModel(session, ovenStatus, dacStatus);
        QuickTest = new QuickTestViewModel(session);
        Config = new ConfigViewModel(session);
        Log = new LogViewModel();
        Settings = new SettingsViewModel(session);

        _currentView = TestRun;

        ShowTestRunCommand  = new RelayCommand(_ => { TestRun.ActivateAutoTest(); CurrentView = TestRun; });
        ShowLongTermStabilityCommand = new RelayCommand(_ => { TestRun.ActivateLongTermStabilityTest(); CurrentView = TestRun; });
        ShowManualCommand   = new RelayCommand(_ => CurrentView = Manual);
        ShowQuickTestCommand = new RelayCommand(_ => CurrentView = QuickTest);
        ShowConfigCommand   = new RelayCommand(_ => CurrentView = Config);
        ShowConfigPlanCommand = new RelayCommand(_ => { Config.SelectedSection = "方案"; CurrentView = Config; });
        ShowConfigSlotsCommand = new RelayCommand(_ => { Config.SelectedSection = "工位"; CurrentView = Config; });
        ShowLogCommand      = new RelayCommand(_ => CurrentView = Log);
        ShowSettingsCommand = new RelayCommand(_ => OpenSettingsWithPassword());

        session.Reconfigured += OnSessionReconfigured;
        session.DevicesRebuilt += OnSessionDevicesRebuilt;

        // 启动后异步检查更新（不阻塞主界面），Gitee 优先 GitHub 备用
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(3000);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _ = Settings.CheckAndInstallUpdateOnStartupAsync();
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Startup", $"自动检查更新失败: {ex.Message}");
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

    private void OpenSettingsWithPassword()
    {
        var password = PromptAdminPassword();
        if (password is null) return;

        if (password == "admin123")
        {
            CurrentView = Settings;
            return;
        }

        MessageBox.Show("管理员密码错误", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string? PromptAdminPassword()
    {
        var prompt = new TextBlock { Text = "请输入管理员密码", FontSize = 13, FontWeight = FontWeights.SemiBold };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var hint = new TextBlock { Text = "进入设置需要管理员权限", FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var box = new PasswordBox
        {
            Width = 240,
            Height = 30,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 16),
            BorderThickness = new Thickness(1)
        };
        box.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");

        var okButton = new Button
        {
            Content = "确定",
            Width = 82,
            Height = 30,
            IsDefault = true
        };
        okButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 82,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true
        };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { okButton, cancelButton }
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { prompt, hint, box, buttons }
        };
        panel.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");

        var ok = false;

        var window = new Window
        {
            Title = "设置",
            Width = 340,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Content = panel
        };
        window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");

        okButton.Click += (_, _) => { ok = true; window.Close(); };
        cancelButton.Click += (_, _) => window.Close();

        box.Focus();
        window.ShowDialog();
        return ok ? box.Password : null;
    }

    public void Dispose()
    {
        Session.Reconfigured -= OnSessionReconfigured;
        Session.DevicesRebuilt -= OnSessionDevicesRebuilt;
        TestRun.Dispose();
        Manual.Dispose();
        QuickTest.Dispose();
        Log.Dispose();
        foreach (var device in Devices)
            device.Dispose();
        Session.Dispose();
    }
}
