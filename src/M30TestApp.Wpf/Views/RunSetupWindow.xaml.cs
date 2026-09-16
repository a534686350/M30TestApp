using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class RunSetupWindow : Window
{
    private DateTime _lastApply = DateTime.MinValue;
    private int _scanSlotIndex;
    private bool _updatingScanSelection;

    public RunSetupWindow(RunSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridScrollHelper.EnableDragAndWheelScroll(RunSlotGrid);
        Loaded += (_, _) =>
        {
            ResetScanSlot();
            RunBarcodeInput.Focus();
        };
        vm.RequestClose += (_, _) =>
        {
            DialogResult = vm.DialogResult;
            Close();
        };
        vm.ScanResetRequested += (_, _) =>
        {
            RunBarcodeInput.Clear();
            RunScanStatusText.Text = "";
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                ResetScanSlot();
                RunBarcodeInput.Focus();
            });
        };
    }

    private void ResetScanSlot()
    {
        _scanSlotIndex = 0;
        SyncGridSelectionToScanIndex();
        UpdateScanSlotLabel();
    }

    /// <summary>
    /// 更新「当前工位」徽标 + 行三态底色（待录入琥珀 / 已录入绿 / 未录入透明）。
    /// 与 ConfigView 的工位页保持同一套扫码语义：高亮看行底色，不看选中态。
    /// </summary>
    private void UpdateScanSlotLabel()
    {
        var total = RunSlotGrid.Items.Count;

        var nextIndex = total == 0 ? -1 : Math.Min(_scanSlotIndex, total - 1);
        for (var i = 0; i < total; i++)
            if (RunSlotGrid.Items[i] is SlotEntry row)
                row.IsNext = i == nextIndex;

        if (total == 0)
        {
            RunScanSlotLabel.Text = "-";
            return;
        }

        if (_scanSlotIndex >= total)
        {
            RunScanSlotLabel.Text = "已完成";
            return;
        }

        if (RunSlotGrid.Items[_scanSlotIndex] is SlotEntry slot)
            RunScanSlotLabel.Text = slot.Slot;
    }

    /// <summary>
    /// 只做**最小滚动**定位，不设置 SelectedItem / CurrentCell：
    /// 一是选中色的主题视觉会盖掉行底色（待录入琥珀就看不见了），
    /// 二是设选中项/当前单元格会触发 DataGrid 自身的 ScrollIntoView，与本方法的滚动互相打架，
    /// 扫码时视野会上下乱跳（录一个跳底、录下一个又回顶）。
    /// </summary>
    private void SyncGridSelectionToScanIndex(int? scrollToIndex = null)
    {
        var index = scrollToIndex ?? _scanSlotIndex;
        if (RunSlotGrid.Items.Count == 0 || index < 0 || index >= RunSlotGrid.Items.Count)
            return;

        _updatingScanSelection = true;
        try
        {
            RunSlotGrid.UpdateLayout();
            DataGridScrollHelper.ScrollToRow(RunSlotGrid, index);
        }
        finally
        {
            _updatingScanSelection = false;
        }
    }

    private void OnRunSlotGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScanSelection) return;

        if (RunSlotGrid.SelectedIndex >= 0)
            _scanSlotIndex = RunSlotGrid.SelectedIndex;

        UpdateScanSlotLabel();
    }

    private void OnRunBarcodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            ApplyRunBarcode();
            e.Handled = true;
        }
    }

    private void OnRunBarcodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        var txt = tb.Text;
        if (txt.Contains('\r') || txt.Contains('\n'))
        {
            tb.Text = txt.Replace("\r", "").Replace("\n", "");
            tb.CaretIndex = tb.Text.Length;
            ApplyRunBarcode();
        }
    }

    private void OnRunBarcodeConfirm(object sender, RoutedEventArgs e) => ApplyRunBarcode();

    /// <summary>把输入框里的型号加进下拉候选（只影响本次运行的候选列表，不改任何配置文件）。</summary>
    private void OnAddReportDevice(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RunSetupViewModel vm) return;

        var name = (vm.ReportDevice ?? "").Trim();
        if (name.Length == 0) return;
        if (vm.ReportDeviceOptions.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase))) return;

        vm.ReportDeviceOptions.Add(name);
        vm.ReportDevice = name;
    }

    private void ApplyRunBarcode()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastApply).TotalMilliseconds < 50) return;

        var barcode = RunBarcodeInput.Text.Trim();
        if (string.IsNullOrEmpty(barcode)) return;
        _lastApply = now;

        if (RunSlotGrid.Items.Count == 0) return;

        if (DataContext is RunSetupViewModel vm && _scanSlotIndex >= RunSlotGrid.Items.Count)
            vm.EnsureSlotCount(_scanSlotIndex + 1);

        _scanSlotIndex = Math.Clamp(_scanSlotIndex, 0, Math.Max(0, RunSlotGrid.Items.Count - 1));
        if (RunSlotGrid.Items[_scanSlotIndex] is not SlotEntry slot) return;

        var filledIndex = _scanSlotIndex;   // 刚录入的那一行
        slot.SerialNo = barcode;
        RunScanStatusText.Text = $"{slot.Slot} -> {barcode}";

        _scanSlotIndex++;                   // 扫码指针前移到下一工位
        UpdateScanSlotLabel();

        // 定位到刚录入的那一行：它就在原处，视野基本不动；只有快扫到视口底部时才滚一行。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            SyncGridSelectionToScanIndex(filledIndex);
            UpdateScanSlotLabel();
        });

        RunBarcodeInput.Clear();
        RunBarcodeInput.Focus();
    }
}
