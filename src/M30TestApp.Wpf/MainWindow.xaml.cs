using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace M30TestApp.Wpf;

/// <summary>
/// 主窗口：工控上位机框架（标题栏 / 菜单栏 / 工具栏 / 导航区 / LED 状态栏）。
/// 交互逻辑仅保留纯视图行为：标题栏时钟、F11 全屏、关于对话框。
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer;

    public MainWindow()
    {
        InitializeComponent();

        // 标题栏实时时钟。
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        _clockTimer.Start();

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            if (DataContext is System.IDisposable disposable)
                disposable.Dispose();
        };

        // F11 在“全屏 (Maximized)”与“默认窗口 (Normal)”之间切换。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        };
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");

    private void ToggleFullscreen() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = ViewModelAssemblyVersion();
        MessageBox.Show(
            $"M30 压力传感器自动测试系统 V2\n版本 {version}\n\n" +
            "压力 / 温度 / 长期稳定性 自动化测试控制台。\n支持 GPIB 与 RS-232 设备，最多 256 工位。",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string ViewModelAssemblyVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
