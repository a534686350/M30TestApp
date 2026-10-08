using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Devices;

namespace M30TestApp.Core.Config;

/// <summary>
/// 一个压力阀门继电器 = 通道地址 + 接线极性。
///
/// 现场接线不统一：总阀 101、阀门 1-8 对应 102-109，但有的阀「OPEN = 开阀」，
/// 有的阀「CLOSE = 开阀」。所以任何地方都不准直接发 ROUT:OPEN / ROUT:CLOSE，
/// 一律走本类，由 Setting.ini 的 [ValveSettings] {Key}Invert 决定实际指令：
///
/// <code>
/// [ValveSettings]
/// MasterValve = "101"
/// Valve1      = "102"
/// ...
/// Valve8      = "109"
/// MasterValveInvert = "False"   ; False = OPEN 开阀（默认）；True = CLOSE 开阀
/// Valve1Invert      = "True"
/// </code>
///
/// 逻辑阀状态与继电器读数的换算用 <see cref="ToValveOpen"/>，
/// 保证界面显示的开/关与指令语义一致。
/// </summary>
public sealed record ValveRelay(string Key, string Address, bool Inverted)
{
    public const string Section = "ValveSettings";

    /// <summary>逻辑阀序号 → 配置键（0 = 总阀，1-8 = 阀门1-8）。</summary>
    public static string KeyForIndex(int index) => index <= 0 ? "MasterValve" : $"Valve{index}";

    /// <summary>配置键 → 极性键。</summary>
    public static string InvertKeyFor(string key) => key + "Invert";

    public static bool ReadInvert(IniFile settings, string key) =>
        bool.TryParse(settings.Get(Section, InvertKeyFor(key), "False"), out var value) && value;

    public static string ReadAddress(IniFile settings, string key, string fallback = "") =>
        (settings.Get(Section, key, fallback) ?? "").Trim();

    public static ValveRelay ForKey(IniFile settings, string key, string fallbackAddress = "") =>
        new(key, ReadAddress(settings, key, fallbackAddress), ReadInvert(settings, key));

    /// <summary>总阀。</summary>
    public static ValveRelay Master(IniFile settings) => ForKey(settings, "MasterValve", "101");

    /// <summary>按阀组号（"5" / "6"…）取阀。</summary>
    public static ValveRelay ForValveNo(IniFile settings, string valveNo) => ForKey(settings, $"Valve{valveNo}");

    /// <summary>按逻辑序号取阀（0 = 总阀 101，1-8 = 阀门1-8 对应 102-109）。</summary>
    public static ValveRelay ForIndex(IniFile settings, int index)
    {
        var key = KeyForIndex(index);
        var fallback = index is >= 0 and <= 8 ? (101 + index).ToString() : "";
        return ForKey(settings, key, fallback);
    }

    /// <summary>地址有效（未配置或占位 "-" 视为无效）。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Address) && Address != "-";

    /// <summary>逻辑「开阀」：按极性决定发 ROUT:OPEN 还是 ROUT:CLOSE。</summary>
    public Task OpenAsync(IDmm dmm, CancellationToken ct = default) =>
        Inverted ? dmm.CloseRelayAsync(Address, ct) : dmm.OpenRelayAsync(Address, ct);

    /// <summary>逻辑「关阀」：按极性决定发 ROUT:CLOSE 还是 ROUT:OPEN。</summary>
    public Task CloseAsync(IDmm dmm, CancellationToken ct = default) =>
        Inverted ? dmm.OpenRelayAsync(Address, ct) : dmm.CloseRelayAsync(Address, ct);

    /// <summary>继电器读数 → 逻辑阀状态（true = 阀开）。</summary>
    public bool ToValveOpen(bool relayClosed) => Inverted ? !relayClosed : relayClosed;

    /// <summary>日志用：实际会下发的指令。</summary>
    public string CommandFor(bool open) => open == Inverted ? "ROUT:CLOSE" : "ROUT:OPEN";
}
