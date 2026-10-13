using System.Collections.ObjectModel;
using M30TestApp.Core.Config;
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

    /// <summary>对应的工位表条目。序列号直接读写它 —— 表格里改了就写回工位表，
    /// 报表 / 导出 / 扫码共用同一份数据，不会出现「界面改了但导出还是旧的」。</summary>
    private readonly SlotEntry? _entry;

    private string _serialNo = "";
    private readonly bool _mirrorEntry;

    public string SerialNo
    {
        get => _mirrorEntry ? _entry!.SerialNo : _serialNo;
        set
        {
            if (_mirrorEntry)
            {
                if (_entry!.SerialNo == value) return;
                _entry.SerialNo = value ?? "";
                OnPropertyChanged();
                return;
            }
            SetField(ref _serialNo, value);
        }
    }

    /// <summary>Cell store keyed by column name. UI binds via custom DataGridTextColumn.</summary>
    public ObservableConcurrentDictionary<string, Cell> Cells { get; } = new();

    public MatrixRowVm(string slot, string serialNo, SlotEntry? entry = null)
    {
        Slot = slot;
        _serialNo = serialNo;
        _entry = entry;
        _mirrorEntry = entry is not null;
    }
}
