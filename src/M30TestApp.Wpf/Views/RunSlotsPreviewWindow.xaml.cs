using System.Collections.Generic;
using System.Windows;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 本轮测试工位只读预览：运行中主窗隐藏、工位表被锁定，
/// 操作员需要核对「这轮到底跑了哪些工位」时用这个窗口。
/// </summary>
public partial class RunSlotsPreviewWindow : Window
{
    public RunSlotsPreviewWindow(IReadOnlyList<SlotEntry> entries)
    {
        InitializeComponent();
        SlotsGrid.ItemsSource = entries;

        var filled = 0;
        foreach (var e in entries)
            if (!SlotLayoutHelper.IsPlaceholderSerial(e.SerialNo)) filled++;

        var range = entries.Count == 0 ? "—" : $"{entries[0].Slot} ~ {entries[^1].Slot}";
        SummaryText.Text = $"共 {entries.Count} 个工位（{range}），已录入序列号 {filled} 个";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
