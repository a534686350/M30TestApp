using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class ConfigView : UserControl
{
    private DateTime _lastApply = DateTime.MinValue;
    private int _scanSlotIndex;
    private bool _updatingScanSelection;
    private bool _scanPositionInitialized;

    private static readonly Brush StatusOkBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush StatusWarnBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public ConfigView()
    {
        InitializeComponent();
        DataGridScrollHelper.EnableDragAndWheelScroll(SlotGrid);

        // 切换 Tab 回来时保留扫码进度；只有行数变化或首次进入才回到当前有效位置。
        Loaded += (_, _) =>
        {
            if (!_scanPositionInitialized)
            {
                _scanPositionInitialized = true;
                ResetScanSlot();
                return;
            }

            if (SlotGrid.Items.Count == 0)
            {
                _scanSlotIndex = 0;
            }
            else if (_scanSlotIndex >= SlotGrid.Items.Count)
            {
                _scanSlotIndex = SlotGrid.Items.Count - 1; // 表缩小了：停在最后一行
            }
            SyncGridSelectionToScanIndex();
            UpdateScanStatusLabels();
        };
    }

    public void EnableSlotsOnlyMode()
    {
        foreach (var item in ConfigTabs.Items)
        {
            if (item is TabItem tab)
                tab.Visibility = string.Equals(tab.Header?.ToString(), "工位", StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        ConfigTabs.SelectedValue = "工位";
        OpenSlotsWindowButton.Visibility = Visibility.Collapsed;
    }

    private void OpenSlotsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        var owner = Window.GetWindow(this);
        if (owner is null) return;

        var window = new SlotsWindow(vm);
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

    private void ResetScanSlot()
    {
        _scanSlotIndex = 0;
        if (SlotGrid.Items.Count > 0)
            SyncGridSelectionToScanIndex();
        UpdateScanStatusLabels();
    }

    /// <summary>更新「下一工位」徽标与「已扫/共」进度。</summary>
    private void UpdateScanStatusLabels()
    {
        var total = SlotGrid.Items.Count;
        var scanned = 0;
        for (var i = 0; i < total; i++)
            if (SlotGrid.Items[i] is SlotEntry s && !string.IsNullOrEmpty(s.SerialNo))
                scanned++;

        ScanProgressLabel.Text = total == 0 ? "" : $"已扫 {scanned} / 共 {total}";

        if (total == 0)
        {
            ScanSlotLabel.Text = "-";
        }
        else if (_scanSlotIndex >= total)
        {
            ScanSlotLabel.Text = "已完成";
        }
        else if (SlotGrid.Items[_scanSlotIndex] is SlotEntry slot)
        {
            ScanSlotLabel.Text = slot.Slot;
        }
    }

    private void SyncGridSelectionToScanIndex(int? scrollToIndex = null)
    {
        var index = scrollToIndex ?? _scanSlotIndex;
        if (SlotGrid.Items.Count == 0 || index < 0 || index >= SlotGrid.Items.Count)
            return;

        _updatingScanSelection = true;
        try
        {
            // 等容器生成完毕再一次性定位：选中 → 当前单元格 → 单次最小滚动。
            // 此前多处滚动相互覆盖（自动滚动 + 贴底计算），导致视口来回跳动。
            SlotGrid.UpdateLayout();
            var item = SlotGrid.Items[index];
            SlotGrid.SelectedItem = item;
            if (SlotGrid.Columns.Count > 0)
                SlotGrid.CurrentCell = new DataGridCellInfo(item, SlotGrid.Columns[0]);
            SlotGrid.ScrollIntoView(item);
        }
        finally
        {
            _updatingScanSelection = false;
        }
    }

    private int CurrentVisibleScanIndex()
    {
        if (SlotGrid.Items.Count == 0) return -1;
        return Math.Clamp(_scanSlotIndex, 0, SlotGrid.Items.Count - 1);
    }

    private void OnSlotGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScanSelection) return;

        if (SlotGrid.SelectedIndex >= 0)
            _scanSlotIndex = SlotGrid.SelectedIndex;

        UpdateScanStatusLabels();
    }

    private void OnSlotGridLoadingRow(object sender, DataGridRowEventArgs e)
        => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

    private void OnBarcodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            ApplyBarcode();
            e.Handled = true;
        }
    }

    private void OnBarcodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        var txt = tb.Text;
        if (txt.Contains('\r') || txt.Contains('\n'))
        {
            tb.Text = txt.Replace("\r", "").Replace("\n", "");
            tb.CaretIndex = tb.Text.Length;
            ApplyBarcode();
        }
    }

    private void OnBarcodeConfirm(object sender, RoutedEventArgs e) => ApplyBarcode();

    private void OnScanResetClick(object sender, RoutedEventArgs e)
    {
        ResetScanSlot();
        ScanStatusText.Text = "已回到第 1 行";
        ScanStatusText.Foreground = StatusOkBrush;
        BarcodeInput.Focus();
    }

    private void ApplyBarcode()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastApply).TotalMilliseconds < 50) return;

        var barcode = BarcodeInput.Text.Trim();
        if (string.IsNullOrEmpty(barcode)) return;
        _lastApply = now;

        // 扫到表格末尾时按需扩展一行（保持扫码连续，不用先去改数量）。
        if (DataContext is ConfigViewModel vm && _scanSlotIndex >= SlotGrid.Items.Count)
            vm.EnsureSlotCount(_scanSlotIndex + 1);

        _scanSlotIndex = Math.Clamp(_scanSlotIndex, 0, Math.Max(0, SlotGrid.Items.Count - 1));
        if (SlotGrid.Items.Count == 0 || SlotGrid.Items[_scanSlotIndex] is not SlotEntry slot) return;

        // 重复序列号拦截：同一编号不允许出现在两个工位。
        for (var i = 0; i < SlotGrid.Items.Count; i++)
        {
            if (i == _scanSlotIndex) continue;
            if (SlotGrid.Items[i] is SlotEntry other &&
                string.Equals(other.SerialNo, barcode, StringComparison.OrdinalIgnoreCase))
            {
                ScanStatusText.Text = $"⚠ 序列号重复：已用于工位 {other.Slot}，未写入";
                ScanStatusText.Foreground = StatusWarnBrush;
                BarcodeInput.SelectAll();
                BarcodeInput.Focus();
                return;
            }
        }

        slot.SerialNo = barcode;
        ScanStatusText.Text = $"{slot.Slot} ← {barcode}";
        ScanStatusText.Foreground = StatusOkBrush;

        _scanSlotIndex++;

        // 单一滚动时机：等布局（含可能的表格扩展重建）稳定后，一次定位到新当前行。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            SyncGridSelectionToScanIndex(CurrentVisibleScanIndex());
            UpdateScanStatusLabels();
        });

        BarcodeInput.Clear();
        BarcodeInput.Focus();
    }
}
