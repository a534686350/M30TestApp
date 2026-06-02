using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;
using TestCheckpointState = M30TestApp.Core.Data.TestCheckpoint;

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

    /// <summary>When true, skip UT data collection.</summary>
    public bool SkipUt { get; set; }

    /// <summary>When true, skip USC data collection.</summary>
    public bool SkipUsc { get; set; }

    /// <summary>When true, skip ISC data collection.</summary>
    public bool SkipIsc { get; set; }

    /// <summary>When true, skip USG data collection.</summary>
    public bool SkipUsg { get; set; }

    /// <summary>When true, skip oven temperature data collection.</summary>
    public bool SkipOvenTemp { get; set; }

    /// <summary>The current ordered column list (TnPm_* keys).</summary>
    public List<string> Columns { get; } = new();

    /// <summary>When set, <see cref="RunPerformanceTestAction"/> resumes from this checkpoint.</summary>
    public TestCheckpointState? ResumeCheckpoint { get; set; }

    /// <summary>Optional soak time override for the resumed temperature point.</summary>
    public int? ResumeSoakMinutesOverride { get; set; }

    public Func<CancellationToken, Task>? PauseWaiter { get; set; }

    public Task WaitIfPausedAsync(CancellationToken ct) =>
        PauseWaiter?.Invoke(ct) ?? Task.CompletedTask;
}
