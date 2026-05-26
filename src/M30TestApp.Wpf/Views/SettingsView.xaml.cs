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
