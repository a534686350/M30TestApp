using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnOpenRepo(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SettingsViewModel.RepoUrl) { UseShellExecute = true }); }
        catch { }
    }

    private void OnOpenIssues(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SettingsViewModel.RepoUrl + "/issues") { UseShellExecute = true }); }
        catch { }
    }

    // ─── 站点标识管理员锁（PasswordBox 不支持绑定，只能在这里取值校验） ──────

    private void OnStationUnlockClick(object sender, RoutedEventArgs e) => TryUnlockStation();

    private void OnStationPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        TryUnlockStation();
    }

    private void TryUnlockStation()
    {
        if (DataContext is not SettingsViewModel vm) return;

        if (vm.TryUnlockStation(StationPasswordBox.Password))
        {
            StationPasswordBox.Clear();
            return;
        }

        var owner = Window.GetWindow(this);
        const string message = "管理员密码不正确，站点标识保持锁定。";
        if (owner is not null)
            AppDialog.Show(owner, message, "上报设备 ID", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppDialog.Show(message, "上报设备 ID", MessageBoxButton.OK, MessageBoxImage.Warning);

        StationPasswordBox.SelectAll();
        StationPasswordBox.Focus();
    }

    /// <summary>「打开在线工作台」：在默认浏览器打开总控制台（地址程序内置）。</summary>
    private void OnOpenStationHubClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        var url = vm.StationHubUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            M30TestApp.Core.Common.AppLog.Warn("Settings", "打开在线工作台失败：" + ex.Message);
        }
    }
}

/// <summary>Converts a non-empty string to Visible, otherwise Collapsed.</summary>
public sealed class NonEmptyToVisibleConverter : IValueConverter
{
    public static readonly NonEmptyToVisibleConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Inverts a boolean value.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is bool b ? !b : value;
}
