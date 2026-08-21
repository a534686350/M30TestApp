using System.Windows;
using System.Windows.Controls;

namespace M30TestApp.Wpf.Views;

public partial class TestRunView : UserControl
{
    public TestRunView()
    {
        InitializeComponent();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e) => ApplyResponsiveLayout();

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded) return;

        // 普通窗口优先保证采集矩阵可见；全屏/大窗口再展开实时日志。
        var compact = ActualHeight < 760 || ActualWidth < 1120;
        LiveLogPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LiveLogRow.Height = compact ? new GridLength(0) : new GridLength(140);
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }
}
