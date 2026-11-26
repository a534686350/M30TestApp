using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 顶部「工位」页签的页面 —— 工位表用电子表格控件（WebView2 + x-spreadsheet），
/// 单元格选择 / 拖动填充 / Excel 复制粘贴 / 改完自动保存。
/// </summary>
public partial class SlotsView : UserControl
{
    private bool _syncing;

    public SlotsView()
    {
        InitializeComponent();
        SlotSheet.RowsChanged += OnSheetRowsChanged;
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
    }

    private SlotsViewModel? Page => DataContext as SlotsViewModel;

    private void Hook()
    {
        if (Page is not { } page) return;
        page.Config.Slots.CollectionChanged -= OnSlotsChanged;
        page.Config.Slots.CollectionChanged += OnSlotsChanged;
        PushSlots();
    }

    private void OnSlotsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => PushSlots();

    private void PushSlots()
    {
        if (Page is not { } page) return;
        _syncing = true;
        try
        {
            SlotSheet.LoadSlots(page.Config.Slots.ToList());
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>表格改完 → 按工位号回写 SlotEntry 并落盘。</summary>
    private void OnSheetRowsChanged(object? sender, IReadOnlyList<SlotSheetRow> rows)
    {
        if (_syncing) return;
        if (Page is not { } page) return;
        if (rows.Count == 0) return;

        var slots = page.Config.Slots;
        if (slots.Count == 0) return;

        // 读回全空说明表格没加载好，不能拿去覆盖内存数据
        if (rows.All(r => string.IsNullOrWhiteSpace(r.serialNo)
                          && string.IsNullOrWhiteSpace(r.board)
                          && string.IsNullOrWhiteSpace(r.channel)))
        {
            AppLog.Warn("UI", "侧边栏工位表读回全空，已忽略本次回写");
            return;
        }

        // 明显错位保护：序列号变成工位号（Slot1/Slot2…）说明表格列被拖乱了，拒绝回写。
        // 之前就是这类错位把 CSV 里 9 行序列号写成了 "Slot1"。
        if (rows.Any(r => r.serialNo.StartsWith("Slot", StringComparison.Ordinal)
                          && r.serialNo.Length > 4 && char.IsDigit(r.serialNo[4])))
        {
            AppLog.Warn("UI", "工位表序列号疑似错位（值为工位号），已忽略本次回写");
            return;
        }

        // 大面积清空保护：粘贴一个空单元格到 19 行会把序列号整片抹掉（老板踩过）。
        // 空值比原来多 3 个以上就认为是误操作，拒绝回写并把表格恢复原样。
        var emptyNow = rows.Count(r => string.IsNullOrWhiteSpace(r.serialNo));
        var emptyBefore = slots.Count(s => string.IsNullOrWhiteSpace(s.SerialNo));
        if (emptyNow > emptyBefore + 3)
        {
            AppLog.Warn("UI", $"序列号被大面积清空（{emptyBefore} → {emptyNow} 个空），已拒绝回写");
            PushSlots();
            if (Page is { } v0) v0.Config.StatusMessage = "检测到序列号被大面积清空，已撤销（要清空请逐行删）";
            return;
        }

        var bySlot = new Dictionary<string, SlotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in slots)
            if (!string.IsNullOrWhiteSpace(s.Slot)) bySlot[s.Slot] = s;

        var changed = 0;
        foreach (var src in rows)
        {
            if (string.IsNullOrWhiteSpace(src.slot)) continue;
            if (!bySlot.TryGetValue(src.slot, out var dst)) continue;

            if (dst.SerialNo != src.serialNo) { dst.SerialNo = src.serialNo; changed++; }
            if (dst.PositionX != src.positionX) { dst.PositionX = src.positionX; changed++; }
            if (dst.PositionY != src.positionY) { dst.PositionY = src.positionY; changed++; }
            if (dst.Board != src.board) { dst.Board = src.board; changed++; }
            if (dst.BoardSlotNo != src.boardSlotNo) { dst.BoardSlotNo = src.boardSlotNo; changed++; }
            if (dst.Fixture != src.fixture) { dst.Fixture = src.fixture; changed++; }
            if (dst.FixtureSlotNo != src.fixtureSlotNo) { dst.FixtureSlotNo = src.fixtureSlotNo; changed++; }
            if (dst.Valve != src.valve) { dst.Valve = src.valve; changed++; }
            if (dst.ValveAddr != src.valveAddr) { dst.ValveAddr = src.valveAddr; changed++; }
            if (dst.PressureController != src.pressureController) { dst.PressureController = src.pressureController; changed++; }
            if (dst.Dmm != src.dmm) { dst.Dmm = src.dmm; changed++; }
            if (dst.Channel != src.channel) { dst.Channel = src.channel; changed++; }
            if (dst.Layer != src.layer) { dst.Layer = src.layer; changed++; }
        }

        if (changed == 0) return;

        page.Config.PersistSlotsAfterEdit();
        page.Config.StatusMessage = $"已保存 {changed} 处修改";
        AppLog.Info("UI", $"侧边栏工位表改动已保存（{changed} 处）");
    }

    private void OpenSlotsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SlotsViewModel page) return;

        var owner = Window.GetWindow(this);
        if (owner is null) return;

        // 独立编辑窗口直接吃 ConfigViewModel（工位数据的唯一持有者）
        var window = new SlotsWindow(page.Config);
        window.Closed += (_, _) =>
        {
            // 独立窗口里改完的序列号不会触发 CollectionChanged（改的是属性），
            // 关窗后必须重新灌一次，否则这边看到的还是旧值。
            PushSlots();
            if (!owner.IsLoaded) return;
            owner.Show();
            owner.Activate();
        };

        owner.Hide();
        window.Show();
        window.Activate();
    }
}
