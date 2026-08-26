using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class ManualView : UserControl
{
    public ManualView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ManualViewModel oldVm)
        {
            oldVm.DataIo.Flushed  -= OnIoFlushed;
            oldVm.History.Flushed -= OnHistoryFlushed;
        }
        if (e.NewValue is ManualViewModel newVm)
        {
            newVm.DataIo.Flushed  += OnIoFlushed;
            newVm.History.Flushed += OnHistoryFlushed;
        }
    }

    private void OnIoFlushed(object? sender, EventArgs e)
    {
        if (DataContext is not ManualViewModel vm || !vm.IoAutoScroll) return;
        ScrollToEnd(DataIoTextBox);
    }

    private void OnHistoryFlushed(object? sender, EventArgs e)
    {
        ScrollToEnd(HistoryTextBox);
    }

    private static void ScrollToEnd(TextBox textBox)
    {
        textBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            textBox.CaretIndex = textBox.Text.Length;
            textBox.ScrollToEnd();
        });
    }
}
