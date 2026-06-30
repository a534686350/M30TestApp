using System;
using System.Windows;

namespace M30TestApp.Wpf.Views;

public partial class UpdateProgressWindow : Window
{
    private bool _allowClose;

    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string message)
    {
        Dispatcher.Invoke(() => StatusText.Text = message);
    }

    public void SetProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            var value = Math.Clamp(percent, 0, 100);
            Progress.IsIndeterminate = false;
            Progress.Value = value;
            PercentText.Text = $"{value}%";
        });
    }

    public void SetIndeterminate(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            Progress.IsIndeterminate = true;
            PercentText.Text = "";
        });
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
