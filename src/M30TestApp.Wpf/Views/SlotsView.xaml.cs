using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 顶部「工位」页签的页面 —— 无扫码的简化工位页。
/// 「独立编辑」要开窗口；序列号列支持直接在表格里改、也支持从 Excel 复制后 Ctrl+V 批量粘贴。
/// </summary>
public partial class SlotsView : UserControl
{
    /// <summary>「序列号」列在 DataGrid.Columns 里的下标（0=工位，1=序列号）。</summary>
    private const int SerialColumnIndex = 1;

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

    /// <summary>单元格编辑结束 → 立刻落盘（现场改完序列号直接跑测试，不会有人再点保存）。</summary>
    private void SlotGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (DataContext is not SlotsViewModel page) return;

        // 提交动作在事件结束时才写回源，推到下一个 UI 周期再保存
        Dispatcher.BeginInvoke(new Action(() =>
        {
            page.Config.PersistSlotsAfterEdit();
            page.Config.StatusMessage = "序列号已保存";
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Ctrl+V：把剪贴板里的多行文本从当前行开始逐行填进「序列号」列。
    ///
    /// 现场典型用法：Excel 里一列序列号（140440-01、140440-02…）复制过来，
    /// 选中起始工位那一格按 Ctrl+V，一次填完。只认序列号列 ——
    /// 其余列是布局参数，批量粘贴会与布局公式打架。
    /// </summary>
    private void SlotGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        if (TryPasteSerialColumn()) e.Handled = true;
    }

    private bool TryPasteSerialColumn()
    {
        if (DataContext is not SlotsViewModel page) return false;
        if (SlotGrid.CurrentColumn is null) return false;

        // 只有序列号列支持批量粘贴
        if (SlotGrid.CurrentColumn.DisplayIndex != SerialColumnIndex)
        {
            if (Clipboard.ContainsText())
            {
                page.Config.StatusMessage = "批量粘贴只支持「序列号」列（其它列是布局参数）";
                AppLog.Info("UI", "工位表批量粘贴被忽略：当前列不是序列号列");
            }
            return false;
        }

        if (!Clipboard.ContainsText()) return false;
        var text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return false;

        var slots = page.Config.Slots;
        if (slots.Count == 0) return false;

        var start = SlotGrid.SelectedIndex >= 0 ? SlotGrid.SelectedIndex : 0;
        if (start >= slots.Count) start = slots.Count - 1;

        var written = 0;
        for (var i = 0; i < lines.Count && start + i < slots.Count; i++)
        {
            slots[start + i].SerialNo = lines[i];
            written++;
        }

        // 立刻落盘：现场改完序列号直接去跑测试，不会有人再点一次保存
        page.Config.PersistSlotsAfterEdit();

        var skipped = lines.Count - written;
        page.Config.StatusMessage = skipped > 0
            ? $"已粘贴 {written} 个序列号，{skipped} 行超出工位表范围未写入"
            : $"已粘贴 {written} 个序列号（从 {slots[start].Slot} 开始）";

        AppLog.Info("UI", page.Config.StatusMessage);
        return true;
    }
}
