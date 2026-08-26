using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace M30TestApp.Wpf.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.LogViewModel old) old.Buffer.Flushed -= OnFlushed;
            if (e.NewValue is ViewModels.LogViewModel vm) vm.Buffer.Flushed += OnFlushed;
        };
    }

    private void OnFlushed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LogTextBox.ScrollToEnd());
}
