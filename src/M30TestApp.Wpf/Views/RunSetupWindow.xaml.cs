using System;
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
    }

    private void ResetScanSlot()
    {
        _scanSlotIndex = 0;
        if (RunSlotGrid.Items.Count == 0)
        {
            UpdateScanSlotLabel();
            return;
        }

        SyncGridSelectionToScanIndex();
        UpdateScanSlotLabel();
    }

    private void UpdateScanSlotLabel()
    {
        if (RunSlotGrid.Items.Count == 0)
        {
            RunScanSlotLabel.Text = "-";
            return;
        }

        if (_scanSlotIndex >= RunSlotGrid.Items.Count)
        {
            RunScanSlotLabel.Text = "已完成";
            return;
        }

        if (RunSlotGrid.Items[_scanSlotIndex] is SlotEntry slot)
            RunScanSlotLabel.Text = slot.Slot;
    }

    private void SyncGridSelectionToScanIndex()
    {
        if (RunSlotGrid.Items.Count == 0 || _scanSlotIndex >= RunSlotGrid.Items.Count)
            return;

        _updatingScanSelection = true;
        try
        {
            RunSlotGrid.SelectedIndex = _scanSlotIndex;
            RunSlotGrid.ScrollIntoView(RunSlotGrid.Items[_scanSlotIndex]);
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
        if (RunSlotGrid.Items.Count == 0 || RunSlotGrid.Items[_scanSlotIndex] is not SlotEntry slot) return;

        slot.SerialNo = barcode;
        RunScanStatusText.Text = $"{slot.Slot} -> {barcode}";

        _scanSlotIndex++;
        UpdateScanSlotLabel();

        var nextIndex = _scanSlotIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (nextIndex < RunSlotGrid.Items.Count)
                SyncGridSelectionToScanIndex();
            UpdateScanSlotLabel();
        });

        RunBarcodeInput.Clear();
        RunBarcodeInput.Focus();
    }
}
