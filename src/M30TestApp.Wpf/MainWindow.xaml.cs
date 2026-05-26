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
        // 按 F11 在“全屏 (Maximized)”与“默认窗口 (Normal)”之间切换。
        // ResizeMode=CanMinimize 已禁止拖拽改变窗口大小。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                ToggleMaximize();
                e.Handled = true;
            }
        };
        StateChanged += (_, _) => UpdateToggleLabel();
        Loaded += (_, _) => UpdateToggleLabel();
    }

    private void OnToggleMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateToggleLabel()
    {
        if (ToggleMaxBtn is null) return;
        ToggleMaxBtn.Content = WindowState == WindowState.Maximized ? "🗗 还原" : "🗖 全屏";
    }
}