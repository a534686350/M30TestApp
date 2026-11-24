using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 通用面板窗口：把主界面里的一块功能卡片独立出来全屏显示（表格能拉大）。
/// 关掉即回主界面。同一面板只开一个实例（静态字段按 key 记录）。
/// </summary>
public sealed partial class PanelWindow : Window
{
    private static readonly Dictionary<string, PanelWindow> s_open = new();

    public string PanelKey { get; private set; } = "";

    public PanelWindow(string title, object panel, string panelKey)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PanelHost.Content = panel;
        PanelKey = panelKey;
        Owner = Application.Current.MainWindow;
        Closed += (_, _) => s_open.Remove(panelKey);
    }

    /// <summary>显示面板（已打开则激活，不重复开窗）。</summary>
    public static void ShowPanel(string title, object panel, string panelKey, double width = 1120, double height = 680)
    {
        if (s_open.TryGetValue(panelKey, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new PanelWindow(title, panel, panelKey) { Width = width, Height = height };
        s_open[panelKey] = window;
        window.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
