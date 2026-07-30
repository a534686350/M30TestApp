using System;
using System.Windows;

namespace M30TestApp.Wpf.Views;

public partial class UpdateProgressWindow : Window
{
    private bool _allowClose;
    private bool _isIndeterminate = true;

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
            _isIndeterminate = false;
            PercentText.Text = $"{value}%";
            SetFillRatio(value / 100d);
        });
    }

    public void SetIndeterminate(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            _isIndeterminate = true;
            PercentText.Text = "";
            SetFillRatio(0.28);
        });
    }

    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetFillRatio(_isIndeterminate ? 0.28 : ParsePercent());
    }

    private double ParsePercent()
    {
        var text = PercentText.Text.TrimEnd('%');
        return double.TryParse(text, out var value)
            ? Math.Clamp(value / 100d, 0, 1)
            : 0;
    }

    private void SetFillRatio(double ratio)
    {
        if (!IsInitialized)
            return;

        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * Math.Clamp(ratio, 0, 1));
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