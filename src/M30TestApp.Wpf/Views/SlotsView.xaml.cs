using System.Windows;
using System.Windows.Controls;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 顶部「工位」页签的页面 —— 无扫码的简化工位页。
/// 只有「独立编辑」这一个动作需要代码（要开窗口），其余全部走绑定。
/// </summary>
public partial class SlotsView : UserControl
{
    public SlotsView() => InitializeComponent();

    private void OpenSlotsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SlotsViewModel page) return;

        var owner = Window.GetWindow(this);
        if (owner is null) return;

        // 独立编辑窗口直接吃 ConfigViewModel（工位数据的唯一持有者）
        var window = new SlotsWindow(page.Config);
        window.Closed += (_, _) =>
        {
            if (!owner.IsLoaded) return;
            owner.Show();
            owner.Activate();
        };

        owner.Hide();
        window.Show();
        window.Activate();
    }
}
