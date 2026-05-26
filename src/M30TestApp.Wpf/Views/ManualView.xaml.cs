using System.Collections.Specialized;
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

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ManualViewModel oldVm)
        {
            ((INotifyCollectionChanged)oldVm.DataIo).CollectionChanged  -= OnDataIoChanged;
            ((INotifyCollectionChanged)oldVm.History).CollectionChanged -= OnHistoryChanged;
        }
        if (e.NewValue is ManualViewModel newVm)
        {
            ((INotifyCollectionChanged)newVm.DataIo).CollectionChanged  += OnDataIoChanged;
            ((INotifyCollectionChanged)newVm.History).CollectionChanged += OnHistoryChanged;
        }
    }

    private void OnDataIoChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not ManualViewModel vm || !vm.IoAutoScroll) return;
        ScrollToEnd(DataIoTextBox);
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
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
