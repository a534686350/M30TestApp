using System;
using System.Collections.Generic;

namespace M30TestApp.Core.Config;

public enum DeviceBackend { Sim, Hw }

public enum DeviceKind
{
    Pressure,
    Oven,
    Dmm,
    Dac,
    Power,
    Board,
    Valve,
    Channel,
    Sample
}

/// <summary>
/// Per-device profile: model name, backend (SIM/HW), and physical address.
/// Loaded from `Setting.ini` and friends. Kept intentionally simple/flat for easy editing.
/// </summary>
public sealed class DeviceProfile
{
    public DeviceKind Kind { get; init; }
    public string Model { get; init; } = "";          // e.g. "FLUKE-7250"
    public DeviceBackend Backend { get; init; }       // SIM or HW
    public string Address { get; init; } = "";        // "GPIB1::5::INSTR" or "COM8"
    public int Baud { get; init; } = 9600;
    public string Parity { get; init; } = "N";
    public int DataBits { get; init; } = 8;
    public string StopBits { get; init; } = "1";

    public override string ToString() =>
        $"{Kind} {Model} [{Backend}] @ {Address}";
}

/// <summary>
/// Aggregate of all device profiles for one test station.
/// </summary>
public sealed class StationProfile
{
    /// <summary>每类设备的主实例（配置段 <c>[Device.X]</c>），保持既有单实例语义。</summary>
    public Dictionary<DeviceKind, DeviceProfile> Devices { get; } = new();

    /// <summary>
    /// 每类设备的全部实例，按配置段顺序排列：<c>[Device.X]</c>、<c>[Device.X2]</c>、<c>[Device.X3]</c>…
    /// 下标 0 恒等于 <see cref="Devices"/> 里的主实例（同一对象引用）。
    /// 目前只有 DMM（DAQ973A）需要多台：万用表自动测试 48 工位 = 3 台 × 16。
    /// </summary>
    public Dictionary<DeviceKind, List<DeviceProfile>> AllUnits { get; } = new();

    /// <summary>单类设备最多允许的实例数（<c>[Device.X]</c> 之外再加 7 个编号段）。</summary>
    public const int MaxUnitsPerKind = 8;

    public DeviceProfile? Get(DeviceKind kind) =>
        Devices.TryGetValue(kind, out var p) ? p : null;

    public DeviceProfile Require(DeviceKind kind) =>
        Get(kind) ?? throw new InvalidOperationException($"Device profile missing: {kind}");

    /// <summary>取某类设备的全部实例；未登记时回退到主实例，主实例也没有则返回空表。</summary>
    public IReadOnlyList<DeviceProfile> Units(DeviceKind kind)
    {
        if (AllUnits.TryGetValue(kind, out var list) && list.Count > 0) return list;
        return Get(kind) is { } p ? new[] { p } : Array.Empty<DeviceProfile>();
    }

    /// <summary>
    /// Loads from Setting.ini. Expected layout (simplified):
    ///   [DefaultLoadClass]
    ///   Pressure = SIM | HW
    ///   Oven     = SIM | HW
    ///   Dmm      = SIM | HW
    ///   Dac      = SIM | HW
    ///   Power    = SIM | HW
    ///   Board    = SIM | HW
    ///
    ///   [Device.Pressure]
    ///   Model    = FLUKE-7250
    ///   Address  = GPIB1::5::INSTR
    ///
    ///   [Device.Oven]
    ///   Model    = GWSEBWT1670
    ///   Address  = COM8
    ///   Baud     = 19200
    ///
    /// 同类多台设备用编号段：<c>[Device.Dmm]</c> / <c>[Device.Dmm2]</c> / <c>[Device.Dmm3]</c>…
    /// 编号段必须连续，遇到第一个不存在的段即停止扫描。
    /// </summary>
    public static StationProfile Load(IniFile ini)
    {
        var p = new StationProfile();
        var sections = new HashSet<string>(ini.Sections, StringComparer.OrdinalIgnoreCase);

        foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
        {
            var baseSection = $"Device.{kind}";
            var units = new List<DeviceProfile> { ReadProfile(ini, kind, baseSection) };
            p.Devices[kind] = units[0];

            for (var i = 2; i <= MaxUnitsPerKind; i++)
            {
                var section = baseSection + i;
                if (!sections.Contains(section)) break;
                units.Add(ReadProfile(ini, kind, section));
            }

            p.AllUnits[kind] = units;
        }
        return p;
    }

    /// <summary>读取单个 <c>[Device.Xn]</c> 段；段不存在时返回默认（SIM、空地址）实例。</summary>
    public static DeviceProfile ReadProfile(IniFile ini, DeviceKind kind, string section)
    {
        var backend = ini.Get("DefaultLoadClass", kind.ToString(), "SIM")
            .Equals("HW", StringComparison.OrdinalIgnoreCase)
            ? DeviceBackend.Hw : DeviceBackend.Sim;

        return new DeviceProfile
        {
            Kind = kind,
            Backend = backend,
            Model = ini.Get(section, "Model"),
            Address = ini.Get(section, "Address"),
            Baud = int.TryParse(ini.Get(section, "Baud", "9600"), out var b) ? b : 9600,
            Parity = ini.Get(section, "Parity", "N"),
            DataBits = int.TryParse(ini.Get(section, "DataBits", "8"), out var d) ? d : 8,
            StopBits = ini.Get(section, "StopBits", "1"),
        };
    }

    /// <summary>第 n 个实例（0-based）对应的 ini 段名：0 → <c>Device.Dmm</c>，1 → <c>Device.Dmm2</c>…</summary>
    public static string SectionName(DeviceKind kind, int index) =>
        index <= 0 ? $"Device.{kind}" : $"Device.{kind}{index + 1}";
}
