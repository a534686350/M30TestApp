using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// 973 直测面板的一行（一个通道）。
/// 三组共用：电压 VOUT1-18（101-118）、电阻 R1-18（201-218）、电源 1-18 + 总电源（301-318 / 319、320）。
/// </summary>
public sealed class DmmChannelRowVm : ViewModelBase
{
    /// <summary>面板内序号（1-based）。</summary>
    public required int Index { get; init; }

    /// <summary>显示名：VOUT1 / R1 / 电源1 / 总电源1。</summary>
    public required string Label { get; init; }

    /// <summary>实际 973 通道号（101 / 201 / 301 / 319）。</summary>
    public required string Channel { get; init; }

    private string _valueText = "—";
    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    private bool _isOn;
    /// <summary>电源行专用：该路是否闭合（上电）。</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetField(ref _isOn, value)) return;
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>电源状态显示文本。</summary>
    public string StateText => IsOn ? "已上电" : "已断开";

    private bool _isError;
    public bool IsError
    {
        get => _isError;
        set => SetField(ref _isError, value);
    }

    /// <summary>是否为总电源行（319/320），界面上单独标一下。</summary>
    public bool IsMaster { get; init; }
}
