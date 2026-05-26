using System.Collections.ObjectModel;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// One row of the live data matrix: slot id + dynamic columns.
/// Cells are observable dictionary entries so the DataGrid auto-refreshes.
/// </summary>
public sealed class MatrixRowVm : ViewModelBase
{
    public string Slot { get; }
    public string SerialNo { get; set; } = "";

    /// <summary>Cell store keyed by column name. UI binds via custom DataGridTextColumn.</summary>
    public ObservableConcurrentDictionary<string, Cell> Cells { get; } = new();

    public MatrixRowVm(string slot, string serialNo)
    {
        Slot = slot;
        SerialNo = serialNo;
    }
}
