using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace M30TestApp.Wpf.Views;

public partial class TestRunView : UserControl
{
    private readonly DispatcherTimer _inactivityTimer;
    private bool _isFullScreenMode = false;

    public TestRunView()
    {
        InitializeComponent();

        // Setup inactivity timer (30 seconds)
        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _inactivityTimer.Tick += OnInactivityTimerTick;

        // Subscribe to mouse move events
        MouseMove += OnMouseMove;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _inactivityTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _inactivityTimer.Stop();
        MouseMove -= OnMouseMove;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Reset timer on any mouse movement
        _inactivityTimer.Stop();
        _inactivityTimer.Start();

        // Restore normal view if in full-screen mode
        if (_isFullScreenMode)
        {
            ExitFullScreenMode();
        }
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        EnterFullScreenMode();
    }

    private void EnterFullScreenMode()
    {
        if (_isFullScreenMode) return;

        _isFullScreenMode = true;

        // Hide KPI strip (Row 0)
        var kpiRow = FindName("KpiRow") as FrameworkElement;
        if (kpiRow != null) kpiRow.Visibility = Visibility.Collapsed;

        // Hide control bar (Row 1)
        var controlBar = FindName("ControlBar") as FrameworkElement;
        if (controlBar != null) controlBar.Visibility = Visibility.Collapsed;

        // Hide test statistics panel (right side of Row 2)
        var statsPanel = FindName("StatsPanel") as FrameworkElement;
        if (statsPanel != null) statsPanel.Visibility = Visibility.Collapsed;

        // Show temperature and pressure overlay
        var tempPressureOverlay = FindName("TempPressureOverlay") as FrameworkElement;
        if (tempPressureOverlay != null) tempPressureOverlay.Visibility = Visibility.Visible;
    }

    private void ExitFullScreenMode()
    {
        if (!_isFullScreenMode) return;

        _isFullScreenMode = false;

        // Restore KPI strip
        var kpiRow = FindName("KpiRow") as FrameworkElement;
        if (kpiRow != null) kpiRow.Visibility = Visibility.Visible;

        // Restore control bar
        var controlBar = FindName("ControlBar") as FrameworkElement;
        if (controlBar != null) controlBar.Visibility = Visibility.Visible;

        // Restore test statistics panel
        var statsPanel = FindName("StatsPanel") as FrameworkElement;
        if (statsPanel != null) statsPanel.Visibility = Visibility.Visible;

        // Hide temperature and pressure overlay
        var tempPressureOverlay = FindName("TempPressureOverlay") as FrameworkElement;
        if (tempPressureOverlay != null) tempPressureOverlay.Visibility = Visibility.Collapsed;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }
}
