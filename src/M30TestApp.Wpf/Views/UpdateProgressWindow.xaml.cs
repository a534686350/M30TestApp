using System;
using System.Windows;

namespace M30TestApp.Wpf.Views;

public partial class UpdateProgressWindow : Window
{
    private bool _allowClose;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        Progress.SizeChanged += (_, _) => UpdateIndicatorWidth();
        Progress.ValueChanged += (_, _) => UpdateIndicatorWidth();
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
            UpdateIndicatorWidth();
        });
    }

    public void SetIndeterminate(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            Progress.IsIndeterminate = true;
            PercentText.Text = "";
            UpdateIndicatorWidth();
        });
    }

    private void UpdateIndicatorWidth()
    {
        if (Progress.IsIndeterminate)
            return;

        Progress.ApplyTemplate();
        if (Progress.Template.FindName("PART_Indicator", Progress) is FrameworkElement indicator)
        {
            var range = Progress.Maximum - Progress.Minimum;
            var ratio = range <= 0 ? 0 : (Progress.Value - Progress.Minimum) / range;
            indicator.Width = Math.Max(0, Progress.ActualWidth * Math.Clamp(ratio, 0, 1));
        }
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
