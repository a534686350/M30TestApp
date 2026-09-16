using System.Windows;
using System.Windows.Controls;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class TestRunView : UserControl
{
    public TestRunView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TestRunViewModel old) old.Log.Flushed -= OnLogFlushed;
            if (e.NewValue is TestRunViewModel vm) vm.Log.Flushed += OnLogFlushed;
        };
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e) => ApplyResponsiveLayout();

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded) return;

        // 采集矩阵优先：窄窗口收窄右侧栏（测试统计 + 实时日志），把宽度让给矩阵。
        var compact = ActualHeight < 760 || ActualWidth < 1120;
        RightRailColumn.Width = new GridLength(compact ? 260 : 330);
    }

    /// <summary>
    /// 仅当滚动条已位于底部时才自动跟随，避免操作员上翻查看历史时被强制拉回底部。
    /// </summary>
    private void OnLogFlushed(object? sender, EventArgs e)
    {
        var sv = FindScrollViewer(LogTextBox);
        if (sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 4)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () =>
                {
                    LogTextBox.CaretIndex = LogTextBox.Text.Length;
                    LogTextBox.ScrollToEnd();
                });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}

