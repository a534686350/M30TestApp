using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>主 DMM，恒等于 <see cref="Dmms"/>[0]；长期稳定性等单台路径沿用此属性。</summary>
    public IDmm Dmm { get; private set; } = null!;

    /// <summary>全部 DMM（DAQ973A）：万用表自动测试 48 工位 = 3 台 × 16。按 <c>[Device.Dmm*]</c> 段顺序。</summary>
    public IReadOnlyList<IDmm> Dmms { get; private set; } = Array.Empty<IDmm>();

    public IDac Dac { get; private set; } = null!;
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

        // 万用表自动测试的通道分配（电压 / 电阻 / 工位电源 / 总电源）来自设置，
        // 让 DmmAutoSlotMap 全局吃到现场配置；配置页保存后会重新赋值。
        DmmAutoSlotMap.Current = DmmAutoChannelConfig.Load(settings ?? new IniFile());

        CreateDevices(DebugMode);

        foreach (var slot in slots.Entries) Matrix.EnsureSlot(slot.Slot);

        Context = new TaskContext
        {
            Pressure = Pressure, Oven = Oven, Dmm = Dmm, Dmms = Dmms, Dac = Dac, Board = Board,
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

        var oldDevices = new List<IDevice> { Pressure, Oven, Dac, Board };
        oldDevices.AddRange(Dmms);
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

    /// <summary>
    /// 按 ini 重新读取设备配置，同步进 <see cref="Station"/> 后重建设备。
    ///
    /// <see cref="Station"/> 的两套字典必须一起同步：<c>Devices</c>（单实例）与
    /// <c>AllUnits</c>（同类多台，如 3 台 DAQ973A）。只同步 <c>Devices</c> 的话，
    /// 配置里写了 3 台 973，重建后仍然只有 1 台。
    ///
    /// 配置页与偏好设置页保存设备配置都必须走这里 —— 以前只有配置页走，
    /// 于是「在偏好设置里改了设备地址」会话内不生效，必须再去配置页点一次保存。
    /// </summary>
    public void ReloadStation(IniFile ini)
    {
        var station = StationProfile.Load(ini);

        Station.Devices.Clear();
        foreach (var kv in station.Devices)
            Station.Devices[kv.Key] = kv.Value;

        Station.AllUnits.Clear();
        foreach (var kv in station.AllUnits)
            Station.AllUnits[kv.Key] = kv.Value;

        RebuildDevices(AppPreferences.DebugMode(ini));
    }

    private void CreateDevices(bool debugMode)
    {
        var factory = new DeviceFactory(Commands, debugMode);
        Pressure = factory.CreatePressure(Station.Require(DeviceKind.Pressure));
        Oven     = factory.CreateOven(Station.Require(DeviceKind.Oven));

        // 同类多台：按 [Device.Dmm]、[Device.Dmm2]、[Device.Dmm3]… 建全部实例；
        // 主实例（下标 0）仍以 Dmm 暴露，单台路径零改动。
        Dmms     = Station.Units(DeviceKind.Dmm).Select(factory.CreateDmm).ToArray();
        Dmm      = Dmms[0];

        Dac      = factory.CreateDac(Station.Require(DeviceKind.Dac));
        Board    = factory.CreateBoard(Station.Require(DeviceKind.Board));
    }

    private void ApplyDevicesToContext()
    {
        Context.Pressure = Pressure;
        Context.Oven = Oven;
        Context.Dmm = Dmm;
        Context.Dmms = Dmms;
        Context.Dac = Dac;
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
        Pressure.Dispose(); Oven.Dispose();
        foreach (var dmm in Dmms) dmm.Dispose();
        Dac.Dispose(); Board.Dispose();
    }
}
