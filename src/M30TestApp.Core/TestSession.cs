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

    public IPressureController Pressure { get; private set; } = null!;
    public IOven Oven { get; private set; } = null!;
    public IDmm Dmm { get; private set; } = null!;
    public IDac Dac { get; private set; } = null!;
    public IPowerSupply Power { get; private set; } = null!;
    public IBoard Board { get; private set; } = null!;

    public DataMatrix Matrix { get; } = new();
    public TaskContext Context { get; }
    public TaskRunner Runner { get; }
    public bool DebugMode { get; private set; }

    public event EventHandler? DevicesRebuilt;

    public TestSession(StationProfile station, CommandDictionary commands, SlotTable slots, TestPlan plan, IniFile? settings = null)
    {
        Station = station;
        Commands = commands;
        Slots = slots;
        Plan = plan;

        DebugMode = AppPreferences.DebugMode(settings ?? new IniFile());
        CreateDevices(DebugMode);

        foreach (var slot in slots.Entries) Matrix.EnsureSlot(slot.Slot);

        Context = new TaskContext
        {
            Pressure = Pressure, Oven = Oven, Dmm = Dmm, Dac = Dac, Power = Power, Board = Board,
            Plan = Plan, Slots = Slots, Matrix = Matrix, Commands = Commands, Settings = settings ?? new IniFile(),
        };
        Runner = new TaskRunner().RegisterBuiltins();
    }

    public void RebuildDevices(bool debugMode)
    {
        if (Context is null)
        {
            DebugMode = debugMode;
            CreateDevices(debugMode);
            return;
        }

        var oldDevices = new IDevice[] { Pressure, Oven, Dmm, Dac, Power, Board };
        DebugMode = debugMode;
        CreateDevices(debugMode);
        ApplyDevicesToContext();

        foreach (var device in oldDevices)
        {
            try { device.Dispose(); }
            catch (Exception ex) { AppLog.Warn("Session", $"Dispose old device failed: {ex.Message}"); }
        }

        AppLog.Info("Session", debugMode
            ? "Debug mode enabled: all devices use simulated backends"
            : "Debug mode disabled: devices use station backends");
        DevicesRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private void CreateDevices(bool debugMode)
    {
        var factory = new DeviceFactory(Commands, debugMode);
        Pressure = factory.CreatePressure(Station.Require(DeviceKind.Pressure));
        Oven     = factory.CreateOven(Station.Require(DeviceKind.Oven));
        Dmm      = factory.CreateDmm(Station.Require(DeviceKind.Dmm));
        Dac      = factory.CreateDac(Station.Require(DeviceKind.Dac));
        Power    = factory.CreatePower(Station.Require(DeviceKind.Power));
        Board    = factory.CreateBoard(Station.Require(DeviceKind.Board));
    }

    private void ApplyDevicesToContext()
    {
        Context.Pressure = Pressure;
        Context.Oven = Oven;
        Context.Dmm = Dmm;
        Context.Dac = Dac;
        Context.Power = Power;
        Context.Board = Board;
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
