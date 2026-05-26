using System;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core;

/// <summary>
/// One-stop session object the UI binds to. Owns the device set, the data matrix
/// and the TaskRunner. The session is single-shot in spirit: call RunAsync once per
/// test pass; reload to start a new one.
/// </summary>
public sealed class TestSession : IDisposable
{
    public StationProfile Station { get; }
    public CommandDictionary Commands { get; }
    public SlotTable Slots { get; private set; }
    public TestPlan Plan { get; set; }

    /// <summary>
    /// Raised after <see cref="ApplyRunConfig"/> swaps in a new <see cref="Plan"/> /
    /// <see cref="Slots"/> pair so the UI can refresh row sets and bound state.
    /// </summary>
    public event EventHandler? Reconfigured;

    public IPressureController Pressure { get; }
    public IOven Oven { get; }
    public IDmm Dmm { get; }
    public IDac Dac { get; }
    public IPowerSupply Power { get; }
    public IBoard Board { get; }

    public DataMatrix Matrix { get; } = new();
    public TaskContext Context { get; }
    public TaskRunner Runner { get; }

    public TestSession(StationProfile station, CommandDictionary commands, SlotTable slots, TestPlan plan, IniFile? settings = null)
    {
        Station = station;
        Commands = commands;
        Slots = slots;
        Plan = plan;

        var factory = new DeviceFactory(commands);
        Pressure = factory.CreatePressure(station.Require(DeviceKind.Pressure));
        Oven     = factory.CreateOven(station.Require(DeviceKind.Oven));
        Dmm      = factory.CreateDmm(station.Require(DeviceKind.Dmm));
        Dac      = factory.CreateDac(station.Require(DeviceKind.Dac));
        Power    = factory.CreatePower(station.Require(DeviceKind.Power));
        Board    = factory.CreateBoard(station.Require(DeviceKind.Board));

        foreach (var slot in slots.Entries) Matrix.EnsureSlot(slot.Slot);

        Context = new TaskContext
        {
            Pressure = Pressure, Oven = Oven, Dmm = Dmm, Dac = Dac, Power = Power, Board = Board,
            Plan = Plan, Slots = Slots, Matrix = Matrix, Commands = Commands, Settings = settings ?? new IniFile(),
        };
        Runner = new TaskRunner().RegisterBuiltins();
    }

    /// <summary>
    /// Swap in the per-run plan + slot table picked from the Run Setup dialog.
    /// Wipes the matrix, reseeds rows, refreshes the <see cref="TaskContext"/> and
    /// raises <see cref="Reconfigured"/> so the UI rebuilds its bound row set.
    /// </summary>
    public void ApplyRunConfig(TestPlan plan, SlotTable slots)
    {
        Plan = plan;
        Slots = slots;
        Matrix.Clear();
        foreach (var s in slots.Entries) Matrix.EnsureSlot(s.Slot);

        Context.Plan = plan;
        Context.Slots = slots;

        AppLog.Info("Session", $"Reconfigured: plan='{plan.Name}', slots={slots.Entries.Count}");
        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Context.Plan = Plan;
        var script = TaskScript.TaskScript.Parse(Plan.TaskScript);
        AppLog.Info("Session", $"Running plan '{Plan.Name}' with {script.Commands.Count} commands");
        await Runner.RunAsync(script, Context, ct).ConfigureAwait(false);
        AppLog.Info("Session", "Run complete");
    }

    public void Dispose()
    {
        Pressure.Dispose(); Oven.Dispose(); Dmm.Dispose();
        Dac.Dispose(); Power.Dispose(); Board.Dispose();
    }
}
