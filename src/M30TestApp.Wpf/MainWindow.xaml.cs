using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace M30TestApp.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is System.IDisposable disposable)
                disposable.Dispose();
        };
        // 按 F11 在“全屏 (Maximized)”与“默认窗口 (Normal)”之间切换。
        // ResizeMode=CanMinimize 已禁止拖拽改变窗口大小。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                e.Handled = true;
            }
        };
    }

}
