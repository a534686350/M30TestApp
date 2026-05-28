using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class ConfigView : UserControl
{
    private DateTime _lastApply = DateTime.MinValue;
    private int _scanSlotIndex;
    private bool _updatingScanSelection;

    public ConfigView()
    {
        InitializeComponent();
        Loaded += (_, _) => ResetScanSlot();
    }

    private void ResetScanSlot()
    {
        _scanSlotIndex = 0;
        if (SlotGrid.Items.Count == 0)
        {
            UpdateScanSlotLabel();
            return;
        }

        SyncGridSelectionToScanIndex();
        UpdateScanSlotLabel();
    }

    private void UpdateScanSlotLabel()
    {
        if (SlotGrid.Items.Count == 0)
        {
            ScanSlotLabel.Text = "-";
            return;
        }

        if (_scanSlotIndex >= SlotGrid.Items.Count)
        {
            ScanSlotLabel.Text = "已完成";
            return;
        }

        if (SlotGrid.Items[_scanSlotIndex] is SlotEntry slot)
            ScanSlotLabel.Text = slot.Slot;
    }

    private void SyncGridSelectionToScanIndex()
    {
        if (SlotGrid.Items.Count == 0 || _scanSlotIndex >= SlotGrid.Items.Count)
            return;

        _updatingScanSelection = true;
        try
        {
            SlotGrid.SelectedIndex = _scanSlotIndex;
            SlotGrid.ScrollIntoView(SlotGrid.Items[_scanSlotIndex]);
        }
        finally
        {
            _updatingScanSelection = false;
        }
    }

    private void OnSlotGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScanSelection) return;

        if (SlotGrid.SelectedIndex >= 0)
            _scanSlotIndex = SlotGrid.SelectedIndex;

        UpdateScanSlotLabel();
    }

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

    private void ApplyBarcode()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastApply).TotalMilliseconds < 50) return;

        var barcode = BarcodeInput.Text.Trim();
        if (string.IsNullOrEmpty(barcode)) return;
        _lastApply = now;

        if (DataContext is ConfigViewModel vm && _scanSlotIndex >= SlotGrid.Items.Count)
            vm.EnsureSlotCount(_scanSlotIndex + 1);

        _scanSlotIndex = Math.Clamp(_scanSlotIndex, 0, Math.Max(0, SlotGrid.Items.Count - 1));
        if (SlotGrid.Items.Count == 0 || SlotGrid.Items[_scanSlotIndex] is not SlotEntry slot) return;

        slot.SerialNo = barcode;
        ScanStatusText.Text = $"{slot.Slot} -> {barcode}";

        _scanSlotIndex++;
        UpdateScanSlotLabel();

        var nextIndex = _scanSlotIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (nextIndex < SlotGrid.Items.Count)
                SyncGridSelectionToScanIndex();
            UpdateScanSlotLabel();
        });

        BarcodeInput.Clear();
        BarcodeInput.Focus();
    }
}
