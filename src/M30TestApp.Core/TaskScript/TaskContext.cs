using System.Collections.Generic;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;

namespace M30TestApp.Core.TaskScript;

/// <summary>
/// Mutable execution context for a TaskRunner pass. Holds device references and
/// the live data matrix being filled.
/// </summary>
public sealed class TaskContext
{
    public IPressureController? Pressure { get; set; }
    public IOven? Oven { get; set; }
    public IDmm? Dmm { get; set; }
    public IDac? Dac { get; set; }
    public IPowerSupply? Power { get; set; }
    public IBoard? Board { get; set; }

    public TestPlan Plan { get; set; } = new();
    public SlotTable Slots { get; set; } = new(System.Array.Empty<SlotEntry>());
    public DataMatrix Matrix { get; set; } = new();
    public CommandDictionary Commands { get; set; } = new(new IniFile());
    public IniFile Settings { get; set; } = new();

    /// <summary>Current temperature point name (e.g. "T1").</summary>
    public string CurrentTemp { get; set; } = "";
    /// <summary>Current pressure point name (e.g. "P1").</summary>
    public string CurrentPressure { get; set; } = "";
    /// <summary>Current supply voltage tag (e.g. "V5").</summary>
    public string CurrentVoltage { get; set; } = "V5";

    /// <summary>When true, skip the leak check phase.</summary>
    public bool SkipLeakCheck { get; set; }

    /// <summary>The current ordered column list (TnPm_* keys).</summary>
    public List<string> Columns { get; } = new();
}
