using System;
using System.Collections.Generic;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// 设置页「设备」一行：一类设备的一个实例（压力控制器 / 烘箱 / DAQ973A #n / 采集板 …）。
///
/// 「启用」开关按设备类别共享（同类所有实例共用 <c>[DefaultLoadClass]</c> 的后端开关），
/// 因此只有该类第一行可编辑，避免出现「973 #1 走硬件、#2 走模拟」这种落不了地的配置。
/// </summary>
public sealed class DeviceConfigVm : ViewModelBase
{
    public required DeviceKind Kind { get; init; }

    /// <summary>同类第几个实例（0-based）。</summary>
    public required int UnitIndex { get; init; }

    /// <summary>该类第一行（承载「启用」开关）。</summary>
    public required bool IsKindFirst { get; init; }

    /// <summary>列表显示名，如「DAQ973A #2」。</summary>
    public required string Title { get; init; }

    /// <summary>用途说明，显示在标题下方。</summary>
    public string Hint { get; init; } = "";

    /// <summary>是否串口设备（只有串口设备显示波特率）。</summary>
    public bool IsSerial { get; init; }

    /// <summary>是否属于可增删的 DAQ973A 组（1-3 台）。</summary>
    public bool IsDmmUnit => Kind == DeviceKind.Dmm;

    private bool _isActive = true;

    /// <summary>
    /// 是否落在当前「DAQ973A 台数」范围内。超出范围的行仍显示（方便看到最多能配几台），
    /// 但界面会灰显，保存时也不会写入 ini。
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private string _model = "";
    public string Model { get => _model; set => SetField(ref _model, value); }

    private string _address = "";

    /// <summary>
    /// 完整地址，最终写回 ini 的就是这个值（<c>GPIB1::9::INSTR</c> / <c>COM5</c>）。
    /// 界面上拆成「总线 / 串口」+「地址号」两列下拉联动，避免手打 <c>::INSTR</c> 打错。
    /// </summary>
    public string Address
    {
        get => _address;
        set
        {
            if (!SetField(ref _address, value)) return;
            var v = value ?? "";
            SetField(ref _bus, SplitBus(v));
            OnPropertyChanged(nameof(Bus));
            SetField(ref _number, SplitNumber(v));
            OnPropertyChanged(nameof(Number));
            OnPropertyChanged(nameof(AddressPreview));
        }
    }

    /// <summary>总线 / 串口候选：GPIB 设备给 GPIB0-3，串口设备给 COM1-8。</summary>
    public IReadOnlyList<string> BusOptions { get; init; } = Array.Empty<string>();

    private string _bus = "";

    /// <summary>总线号或串口号，如 <c>GPIB1</c> / <c>COM5</c>。</summary>
    public string Bus
    {
        get => _bus;
        set
        {
            var next = (value ?? "").Trim();
            if (!SetField(ref _bus, next)) return;
            SetField(ref _address, Compose(next, _number));
            OnPropertyChanged(nameof(Address));
            OnPropertyChanged(nameof(AddressPreview));
        }
    }

    private string _number = "";

    /// <summary>GPIB 地址号（0-30）；串口设备为空。</summary>
    public string Number
    {
        get => _number;
        set
        {
            var next = (value ?? "").Trim();
            if (!SetField(ref _number, next)) return;
            SetField(ref _address, Compose(_bus, next));
            OnPropertyChanged(nameof(Address));
            OnPropertyChanged(nameof(AddressPreview));
        }
    }

    /// <summary>按当前「总线 + 地址号」拼出的完整地址，只读回显用。</summary>
    public string AddressPreview => Compose(_bus, _number);

    /// <summary>串口设备只有 COM 口、没有地址号；GPIB 设备形如 <c>GPIB1::9::INSTR</c>。</summary>
    /// <remarks>
    /// 地址号没填时用 <c>?</c> 占位（<c>GPIB2::?::INSTR</c>）而不是拼出 <c>GPIB2::::INSTR</c> ——
    /// 后者看着像配好了，实际设备根本连不上。
    /// </remarks>
    private static string Compose(string bus, string number)
    {
        if (string.IsNullOrWhiteSpace(bus)) return "";
        if (bus.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return bus;
        return $"{bus}::{(string.IsNullOrWhiteSpace(number) ? "?" : number)}::INSTR";
    }

    /// <summary>地址是否配完整（总线非空，且 GPIB 设备填了地址号）。</summary>
    public bool IsAddressComplete =>
        !string.IsNullOrWhiteSpace(_bus)
        && (_bus.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(_number));

    private static string SplitBus(string address)
    {
        var s = (address ?? "").Trim();
        if (s.Length == 0) return "";
        var sep = s.IndexOf("::", StringComparison.Ordinal);
        return sep > 0 ? s[..sep].Trim() : s;
    }

    private static string SplitNumber(string address)
    {
        var s = (address ?? "").Trim();
        var sep = s.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0) return "";
        var rest = s[(sep + 2)..];
        var end = rest.IndexOf("::", StringComparison.Ordinal);
        return (end >= 0 ? rest[..end] : rest).Trim();
    }

    private int _baud = 9600;
    public int Baud { get => _baud; set => SetField(ref _baud, value); }

    // 串口细节参数：界面不暴露，保存时按原值写回，避免被默认值覆盖。
    public string Parity { get; init; } = "N";
    public int DataBits { get; init; } = 8;
    public string StopBits { get; init; } = "1";
}
