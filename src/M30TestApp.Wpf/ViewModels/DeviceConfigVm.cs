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

    /// <summary>非 973 的同类第一行 —— 只有这一行显示「HW/SIM 后端」勾选框。</summary>
    public bool IsNonDmmKindFirst => !IsDmmUnit && IsKindFirst;

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

    private bool _unitEnabled = true;
    /// <summary>
    /// 该台设备是否启用（**每台独立**，只用于 DAQ973A 这类多台设备）。
    /// false = 不写入 ini 的设备段、不参与采集；和 <see cref="Enabled"/>（同类共用 HW/SIM 后端开关）不是一回事。
    /// </summary>
    public bool UnitEnabled
    {
        get => _unitEnabled;
        set => SetField(ref _unitEnabled, value);
    }

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
            OnPropertyChanged(nameof(IsUsb));
        }
    }

    /// <summary>总线 / 串口候选：GPIB 设备给 GPIB0-3 + USB0，串口设备给 COM1-8。</summary>
    public IReadOnlyList<string> BusOptions { get; init; } = Array.Empty<string>();

    private string _bus = "";

    /// <summary>总线号或串口号，如 <c>GPIB1</c> / <c>USB0</c> / <c>COM5</c>。</summary>
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
            OnPropertyChanged(nameof(IsUsb));
        }
    }

    private string _number = "";

    /// <summary>
    /// GPIB 地址号（0-30）；USB 设备是 <c>0x2A8D::0x8601::MY59018500::0</c> 这一整段；
    /// 串口设备为空。
    /// </summary>
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

    /// <summary>当前行是不是 USB 设备（USB 资源串整段保存，不按 GPIB 规则重拼）。</summary>
    public bool IsUsb => IsUsbBus(_bus);

    /// <summary>串口设备只有 COM 口、没有地址号；GPIB 设备形如 <c>GPIB1::9::INSTR</c>；
    /// USB 设备形如 <c>USB0::0x2A8D::0x8601::MY59018500::0::INSTR</c>。</summary>
    /// <remarks>
    /// 地址号没填时用 <c>?</c> 占位（<c>GPIB2::?::INSTR</c>）而不是拼出 <c>GPIB2::::INSTR</c> ——
    /// 后者看着像配好了，实际设备根本连不上。
    /// </remarks>
    private static string Compose(string bus, string number)
    {
        if (string.IsNullOrWhiteSpace(bus)) return "";
        var n = (number ?? "").Trim();

        if (bus.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return bus;

        if (IsUsbBus(bus))
        {
            // 用户可能把整串粘进地址号里（USB0::0x2A8D::...::INSTR），那就原样用，别重复加前缀
            if (n.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return n;
            // 从 GPIB 切过来时地址号还是 "9" 这种纯数字，不能拼成 USB0::9 —— 那是残缺地址，
            // 反而让人以为配好了。留 "USB0::" 提示还得补厂商/序列号段。
            if (n.Length == 0 || !n.Contains("::", StringComparison.Ordinal)) return bus + "::";
            return bus + "::" + n;
        }

        // 反过来：从 USB 切回 GPIB 时地址号是 "0x2A8D::..."，同样不能直接拼。
        if (n.Length == 0 || n.Contains("::", StringComparison.Ordinal)) return $"{bus}::?::INSTR";
        return $"{bus}::{n}::INSTR";
    }

    private static bool IsUsbBus(string? bus) =>
        !string.IsNullOrWhiteSpace(bus) && bus.StartsWith("USB", StringComparison.OrdinalIgnoreCase);

    /// <summary>地址是否配完整（总线非空；GPIB 要地址号，USB 要带厂商/序列号段，串口不用）。</summary>
    public bool IsAddressComplete =>
        !string.IsNullOrWhiteSpace(_bus)
        && (_bus.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || (IsUsbBus(_bus)
                ? !string.IsNullOrWhiteSpace(_number) && _number.Contains("::", StringComparison.Ordinal)
                : !string.IsNullOrWhiteSpace(_number)));

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

        // USB 资源串是「总线::厂商::型号::序列号::接口::INSTR」，中间全是 ::，
        // 必须整段保留，不能像 GPIB 那样只取第一段。
        if (IsUsbBus(s[..sep]))
        {
            if (rest.EndsWith("::INSTR", StringComparison.OrdinalIgnoreCase))
                rest = rest[..^7];
            return rest.Trim();
        }

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
