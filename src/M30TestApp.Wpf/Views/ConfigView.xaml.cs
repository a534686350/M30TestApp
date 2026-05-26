using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Views;

public partial class ConfigView : UserControl
{
    public ConfigView() => InitializeComponent();

    private void OnSlotGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlotGrid.SelectedItem is SlotEntry slot)
            ScanSlotLabel.Text = slot.Slot;
    }

    private void OnBarcodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyBarcode();
            e.Handled = true;
        }
    }

    private void OnBarcodeConfirm(object sender, RoutedEventArgs e) => ApplyBarcode();

    private void ApplyBarcode()
    {
        var barcode = BarcodeInput.Text.Trim();
        if (string.IsNullOrEmpty(barcode)) return;

        if (SlotGrid.SelectedItem is not SlotEntry slot) return;

        slot.SerialNo = barcode;
        ScanStatusText.Text = $"{slot.Slot} -> {barcode}";

        // refresh DataGrid row display
        SlotGrid.Items.Refresh();

        // auto-advance to next row
        var idx = SlotGrid.SelectedIndex;
        if (idx + 1 < SlotGrid.Items.Count)
        {
            SlotGrid.SelectedIndex = idx + 1;
            SlotGrid.ScrollIntoView(SlotGrid.SelectedItem);
            ScanSlotLabel.Text = ((SlotEntry)SlotGrid.SelectedItem).Slot;
        }

        // clear input, keep focus for next scan
        BarcodeInput.Clear();
        BarcodeInput.Focus();
    }
}
