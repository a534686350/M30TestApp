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
    public Dictionary<DeviceKind, DeviceProfile> Devices { get; } = new();

    public DeviceProfile? Get(DeviceKind kind) =>
        Devices.TryGetValue(kind, out var p) ? p : null;

    public DeviceProfile Require(DeviceKind kind) =>
        Get(kind) ?? throw new InvalidOperationException($"Device profile missing: {kind}");

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
    /// </summary>
    public static StationProfile Load(IniFile ini)
    {
        var p = new StationProfile();
        foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
        {
            var backend = ini.Get("DefaultLoadClass", kind.ToString(), "SIM")
                .Equals("HW", StringComparison.OrdinalIgnoreCase)
                ? DeviceBackend.Hw : DeviceBackend.Sim;

            var section = $"Device.{kind}";
            var profile = new DeviceProfile
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
            p.Devices[kind] = profile;
        }
        return p;
    }
}
