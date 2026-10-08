using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;
using LongTermStabilityRunner = M30TestApp.Core.TaskScript.LongTermStabilityRunner;

namespace M30TestApp.Core.TaskScript.Actions;

internal static class A
{
    public static float ParseFloat(string s) =>
        float.Parse(s, CultureInfo.InvariantCulture);

    public static int ParseInt(string s) =>
        int.Parse(s, CultureInfo.InvariantCulture);
}

// ─── Initial:* ──────────────────────────────────────────────────────────────
public sealed class InitialPressureAction : IAction
{
    public string Key => "Initial:Pressure";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Pressure is null) { AppLog.Warn("Init", "No pressure device"); return; }
        await ctx.Pressure.OpenAsync(ct);
        await ctx.Pressure.SetMeasureAsync(ct);
        AppLog.Info("Init", $"Pressure ready: {ctx.Pressure.Model}");
    }
}
public sealed class InitialOvenAction : IAction
{
    public string Key => "Initial:Temp";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Oven is null) return;
        await ctx.Oven.OpenAsync(ct);
        AppLog.Info("Init", $"Oven ready: {ctx.Oven.Model}");
    }
}
public sealed class InitialBoardAction : IAction
{
    public string Key => "Initial:Board";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Dac is not null) await ctx.Dac.OpenAsync(ct);
        if (ctx.Board is not null)
        {
            var sharesPort = ctx.Dac is not null &&
                             string.Equals(ctx.Board.Address, ctx.Dac.Address, StringComparison.OrdinalIgnoreCase);
            if (sharesPort)
                AppLog.Info("Init", $"Board open skipped: shared serial port {ctx.Board.Address} with Dac");
            else
                await ctx.Board.OpenAsync(ct);
        }
        AppLog.Info("Init", "Board/Dac ready");
    }
}
public sealed class InitialDmmAction : IAction
{
    public string Key => "Initial:DMM";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Dmm is not null) await ctx.Dmm.OpenAsync(ct);
        AppLog.Info("Init", $"DMM ready: {ctx.Dmm?.Model}");
    }
}
public sealed class InitialCommuTestAction : IAction
{
    public string Key => "Initial:CommuTest";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        async Task PingAsync(IDevice? d)
        {
            if (d is null) return;
            await d.SelfTestAsync(ct);
        }
        await PingAsync(ctx.Pressure);
        await PingAsync(ctx.Oven);
        await PingAsync(ctx.Dac);
        AppLog.Info("Init", "Communication self-test done");
    }
}

// ─── DAQ:* ──────────────────────────────────────────────────────────────────
public sealed class DaqClearDataAction : IAction
{
    public string Key => "DAQ:ClearData";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        ctx.Matrix.Clear();
        ctx.Columns.Clear();
        foreach (var slot in ctx.Slots.Entries) ctx.Matrix.EnsureSlot(slot.Slot);
        AppLog.Info("DAQ", "Matrix cleared");
        return Task.CompletedTask;
    }
}
public sealed class DaqTestTypeAction : IAction
{
    public string Key => "DAQ:TestType";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var t = cmd.Args.Count > 0 ? cmd.Args[0] : "";
        AppLog.Info("DAQ", $"TestType={t}");
        return Task.CompletedTask;
    }
}
public sealed class DaqDownAction : IAction
{
    public string Key => "DAQ:Down";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        // 旧脚本仍可能包含 DAQ:Down；外部电源已从运行链路移除，
        // 保留动作键只是为了兼容旧方案，不再发送任何电源指令。
        AppLog.Info("DAQ", "DAQ:Down 已兼容处理（外部电源功能已移除）");
        return Task.CompletedTask;
    }
}

// ─── TP:* (Test Point) ─────────────────────────────────────────────────────
public sealed class TpSetPressurePointAction : IAction
{
    public string Key => "TP:SetPressurePoint";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        // args: <index>[,<mode>]
        if (cmd.Args.Count == 0) throw new ArgumentException("TP:SetPressurePoint requires index");
        var idx = A.ParseInt(cmd.Args[0]);
        if (idx < 1 || idx > ctx.Plan.PressurePoints.Count)
        {
            AppLog.Warn("TP", $"Pressure point index out of range: {idx}");
            return;
        }
        var pp = ctx.Plan.PressurePoints[idx - 1];
        ctx.CurrentPressure = pp.Name;
        if (ctx.Pressure is not null)
        {
            // 自动切换压力类型
            await ctx.Pressure.SetPressureTypeAsync(pp.PressureType, ct);
            AppLog.Info("TP", $"压力类型切换为 {pp.PressureType} ({pp.PressureTypeDisplay})");

            await ctx.Pressure.SetPressureAsync(pp.Value, ctx.Plan.PressureUnit, ctx.Plan.Precision, ct);
            // wait for stable (simplified)
            var stable = false;
            for (int i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                var v = await ctx.Pressure.ReadPressureAsync(ct);
                if (Math.Abs(v - pp.Value) <= ctx.Plan.Precision)
                {
                    stable = true;
                    break;
                }
                await Task.Delay(50, ct);
            }
            if (!stable)
                throw new TimeoutException($"压力点 {pp.Name} 在规定时间内未稳定：目标 {pp.Value}{ctx.Plan.PressureUnit}");
        }
        AppLog.Info("TP", $"Pressure point {pp.Name}={pp.Value}{ctx.Plan.PressureUnit} [{pp.PressureTypeDisplay}]");
    }
}
public sealed class TpSetTempPointAction : IAction
{
    public string Key => "TP:SetTempPoint";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (cmd.Args.Count == 0) throw new ArgumentException("TP:SetTempPoint requires index");
        var idx = A.ParseInt(cmd.Args[0]);
        if (idx < 1 || idx > ctx.Plan.TempPoints.Count) return;
        var tp = ctx.Plan.TempPoints[idx - 1];
        ctx.CurrentTemp = $"{tp.Name} ({tp.Celsius} °C)";
        if (ctx.Oven is not null)
        {
            await RunPerformanceTestAction.SetAndWaitOvenAsync(ctx, tp, "TP", ct);
        }
        var soakMinutes = tp.SoakMinutes ?? A.ParseInt(ctx.Settings.Get("DelaySettings", "SoakMinutes", "120"));
        if (soakMinutes > 0)
        {
            AppLog.Info("TP", $"Temp point {tp.Name}={tp.Celsius}°C soak {soakMinutes} min");
            await RunPerformanceTestAction.SoakWithLogAsync(ctx, soakMinutes, tp.Name, ct);
        }
        else
        {
            AppLog.Info("TP", $"Temp point {tp.Name}={tp.Celsius}°C");
        }
    }
}
public sealed class TpVentAction : IAction
{
    public string Key => "TP:Vent";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Pressure is not null) await ctx.Pressure.VentAsync(ct);
        AppLog.Info("TP", "Vent");
    }
}
public sealed class TpReturnRoomTempAction : IAction
{
    public string Key => "TP:ReturnRoomTemp";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Oven is not null) await ctx.Oven.SetTempAsync(25f, ct);
        AppLog.Info("TP", "Return room temp");
    }
}
public sealed class TpStopTempAction : IAction
{
    public string Key => "TP:StopTemp";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Oven is not null) await ctx.Oven.StopAsync(ct);
        AppLog.Info("TP", "Oven stopped");
    }
}

// ─── Read:* ─────────────────────────────────────────────────────────────────
internal static class ReadHelper
{
    public static async Task ReadAndStoreAsync(
        TaskContext ctx, string measure,
        Func<IDac, SlotEntry, CancellationToken, Task<float>> dacRead,
        Func<IDmm,  SlotEntry, CancellationToken, Task<double>>? dmmRead,
        CancellationToken ct)
    {
        var col = $"{ctx.CurrentTemp}{ctx.CurrentPressure}_{measure}";
        if (!ctx.Columns.Contains(col)) ctx.Columns.Add(col);
        var dmmSlotDelayMs = dmmRead is null ? 0 : GetDmmSlotDelayMs(ctx);

        // 所需设备缺失时整列标记 Error，绝不把初值 0 当有效测量值写入矩阵。
        // （历史缺陷：value 初值为 0 且两分支都不执行时，会静默写出一列"合法的 0"。）
        string? missingDevice =
            (dmmRead is not null && ctx.Dmm is null) ? "DMM" :
            (dacRead is not null && ctx.Dac is null) ? "DAC" :
            (dacRead is null && dmmRead is null) ? "读取回调" : null;
        if (missingDevice is not null)
            AppLog.Error("Read", $"{measure}: {missingDevice} 不可用，列 {col} 将全部标记为 Error");

        foreach (var slot in ctx.Slots.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                double value = double.NaN;
                if (dacRead is not null && ctx.Dac is not null)
                {
                    value = await dacRead(ctx.Dac, slot, ct);
                }
                else if (dmmRead is not null && ctx.Dmm is not null)
                {
                    value = await dmmRead(ctx.Dmm, slot, ct);
                }
                var ok = !double.IsNaN(value);
                ctx.Matrix.Set(slot.Slot, col, value, ok ? CellStatus.Ok : CellStatus.Error);
                AppLog.Info("Read", $"{slot.Slot} SN={slot.SerialNo} CH={slot.Channel} {col}={value:G6}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"{measure} {slot.Slot} failed: {ex.Message}");
                ctx.Matrix.Set(slot.Slot, col, double.NaN, CellStatus.Error);
            }

            if (dmmSlotDelayMs > 0)
                await Task.Delay(dmmSlotDelayMs, ct);
        }
        AppLog.Info("Read", $"{measure} @ {col} for {ctx.Slots.Entries.Count} slots");
    }

    private static int GetDmmSlotDelayMs(TaskContext ctx) =>
        int.TryParse(ctx.Settings.Get("DelaySettings", "DmmSlotDelayMs", "100"), out var ms)
            ? Math.Max(0, ms)
            : 100;
}
public sealed class ReadRAction : IAction
{
    public string Key => "Read:R";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct) =>
        ReadHelper.ReadAndStoreAsync(ctx, "R",
            null!,
            async (dmm, slot, c) => await dmm.ReadResistanceAsync(slot.Channel, c), ct);
}
public sealed class ReadUsourceAction : IAction
{
    public string Key => "Read:Usource";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var col = string.IsNullOrEmpty(ctx.CurrentTemp) ? "USC" : $"{ctx.CurrentTemp}_USC";
        var p = ctx.Plan.PressurePoints.FirstOrDefault(pp => pp.Name == ctx.CurrentPressure)?.Value ?? 0;
        var t = ctx.Plan.TempPoints.FirstOrDefault(tp => tp.Name == ctx.CurrentTemp)?.Celsius ?? 25;
        await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Usource, col, p, t, ct);
    }
}
public sealed class ReadIsourceAction : IAction
{
    public string Key => "Read:Isource";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var col = string.IsNullOrEmpty(ctx.CurrentTemp) ? "ISC" : $"{ctx.CurrentTemp}_ISC";
        var p = ctx.Plan.PressurePoints.FirstOrDefault(pp => pp.Name == ctx.CurrentPressure)?.Value ?? 0;
        var t = ctx.Plan.TempPoints.FirstOrDefault(tp => tp.Name == ctx.CurrentTemp)?.Celsius ?? 25;
        await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Isource, col, p, t, ct);
    }
}
public sealed class ReadUtAction : IAction
{
    public string Key => "Read:UT";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var col = string.IsNullOrEmpty(ctx.CurrentTemp) ? "UT" : $"{ctx.CurrentTemp}_UT";
        var p = ctx.Plan.PressurePoints.FirstOrDefault(pp => pp.Name == ctx.CurrentPressure)?.Value ?? 0;
        var t = ctx.Plan.TempPoints.FirstOrDefault(tp => tp.Name == ctx.CurrentTemp)?.Celsius ?? 25;
        await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.UT, col, p, t, ct);
    }
}
public sealed class ReadUsignAction : IAction
{
    public string Key => "Read:Usign";
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var col = string.IsNullOrEmpty(ctx.CurrentTemp) || string.IsNullOrEmpty(ctx.CurrentPressure)
            ? "USG"
            : $"{ctx.CurrentTemp}{ctx.CurrentPressure}_USG";
        var p = ctx.Plan.PressurePoints.FirstOrDefault(pp => pp.Name == ctx.CurrentPressure)?.Value ?? 0;
        var t = ctx.Plan.TempPoints.FirstOrDefault(tp => tp.Name == ctx.CurrentTemp)?.Celsius ?? 25;
        await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Usig, col, p, t, ct);
    }
}
public sealed class ReadDmmSampleAction : IAction
{
    public string Key => "Read:DMMSample";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct) =>
        ReadHelper.ReadAndStoreAsync(ctx, "DMM_mV",
            null!,
            async (dmm, slot, c) => await dmm.ReadVoltageAsync(slot.Channel, c) * 1000.0, ct);
}

public sealed class RunPerformanceTestAction : IAction
{
    /// <summary>
    /// 一个阀组 = 阀门继电器（含接线极性）+ 该阀负责的工位。
    /// 开关一律走 <see cref="ValveRelay"/>，由 [ValveSettings] 的 {Key}Invert 决定实际指令。
    /// </summary>
    private sealed record ValveGroup(ValveRelay Relay, List<SlotEntry> Slots)
    {
        public string ValveNo => Relay.Key.StartsWith("Valve", StringComparison.OrdinalIgnoreCase)
            ? Relay.Key["Valve".Length..]
            : Relay.Key;

        public string Address => Relay.Address;

        public bool Inverted => Relay.Inverted;

        public Task OpenAsync(TaskContext ctx, CancellationToken ct) => Relay.OpenAsync(ctx.Dmm!, ct);

        public Task CloseAsync(TaskContext ctx, CancellationToken ct) => Relay.CloseAsync(ctx.Dmm!, ct);
    }

    public string Key => "Run:PerformanceTest";

    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var resume = ctx.ResumeCheckpoint;
        var startTi = resume?.TempIndex ?? 0;
        var startPi = resume?.PressureIndex ?? 0;
        var leakDone = resume?.LeakCheckDone ?? false;

        if (resume is null)
        {
            ctx.Matrix.Clear();
            ctx.Columns.Clear();
            foreach (var slot in ctx.Slots.Entries) ctx.Matrix.EnsureSlot(slot.Slot);
        }
        else
        {
            TestCheckpoint.RestoreMatrix(ctx, resume);
            AppLog.Info("Run", $"续测：从温度点 #{startTi + 1}、压力点 #{startPi + 1} 继续");
        }

        AppLog.Info("Run", $"开始完整性能测试：方案={ctx.Plan.Name}，传感器={ctx.Plan.SensorType}，工位数={ctx.Slots.Entries.Count}");

        // ── 0. 初始化设备 ────────────────────────────────────────
        await InitDevicesAsync(ctx, ct);

        // ── 1. 探漏 ──────────────────────────────────────────────
        if (!leakDone && !ctx.SkipLeakCheck)
            await PerformLeakCheckAsync(ctx, ct);
        else if (leakDone)
            AppLog.Info("Run", "续测：跳过探漏（已完成）");

        leakDone = true;
        TestCheckpoint.Save(ctx, startTi, startPi, leakDone);
        await PreparePressureValveRoutingAsync(ctx, ct);

        // ── 2. 逐温度点 ─────────────────────────────────────────
        for (var ti = startTi; ti < ctx.Plan.TempPoints.Count; ti++)
        {
            await ctx.WaitIfPausedAsync(ct);
            var tp = ctx.Plan.TempPoints[ti];
            var piStart = ti == startTi ? startPi : 0;
            ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";
            ctx.CurrentPressure = "";
            AppLog.Info("Run", $"当前温度点：{tp.Name} ({ti + 1}/{ctx.Plan.TempPoints.Count})");

            var skipPrePressure = piStart > 0;

            if (!skipPrePressure)
            {
                if (ctx.Oven is not null && ctx.Oven.State == ConnectionState.Connected)
                {
                    await SetAndWaitOvenAsync(ctx, tp, "Run", ct);

                    var soakMinutes = tp.SoakMinutes ?? (int.TryParse(ctx.Settings.Get("DelaySettings", "SoakMinutes", "120"), out var sm) ? sm : 120);
                    if (resume is not null && ti == startTi && piStart == 0 && ctx.ResumeSoakMinutesOverride is { } resumeSoakMinutes)
                    {
                        soakMinutes = Math.Max(0, resumeSoakMinutes);
                        AppLog.Info("Run", $"Resume soak time override: {soakMinutes} min");
                    }
                    AppLog.Info("Run", $"开始保持温度，持续 {soakMinutes} min");
                    await SoakWithLogAsync(ctx, soakMinutes, tp.Name, ct);
                    AppLog.Info("Run", "保持温度完成");
                }
                else
                {
                    AppLog.Info("Run", "烘箱未连接或未启用，跳过温度设置和保温");
                }

                ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";

                if (!ctx.SkipUt)
                {
                    AppLog.Info("Run", $"开始采集 {tp.Name} UT（全部工位，手动批量逻辑）");
                    await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.UT, $"{tp.Name}_UT", 0, tp.Celsius, ct);
                    AppLog.Info("Run", $"{tp.Name} UT 采集完成");
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name} UT 采集（用户选择）");
                }

                if (!ctx.SkipUsc)
                {
                    AppLog.Info("Run", $"开始采集 {tp.Name} USC（全部工位，手动批量逻辑）");
                    await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Usource, $"{tp.Name}_USC", 0, tp.Celsius, ct);
                    AppLog.Info("Run", $"{tp.Name} USC 采集完成");
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name} USC 采集（用户选择）");
                }

                if (!ctx.SkipIsc)
                {
                    AppLog.Info("Run", $"开始采集 {tp.Name} ISC（全部工位，手动批量逻辑）");
                    await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Isource, $"{tp.Name}_ISC", 0, tp.Celsius, ct);
                    AppLog.Info("Run", $"{tp.Name} ISC 采集完成");
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name} ISC 采集（用户选择）");
                }
            }
            else
            {
                AppLog.Info("Run", $"续测：跳过 {tp.Name} 烘箱/UT/USC/ISC（压力点 {piStart + 1} 起）");
                ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";
            }

            for (var pi = piStart; pi < ctx.Plan.PressurePoints.Count; pi++)
            {
                await ctx.WaitIfPausedAsync(ct);
                var pp = ctx.Plan.PressurePoints[pi];
                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";
                AppLog.Info("Run", $"开始测量当前温度点第{pi + 1}压力点：{tp.Name}:{tp.Celsius}; {pp.Name}:{pp.Value} [{pp.PressureTypeDisplay}]; V5:5; F:USG");

                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";

                if (!ctx.SkipUsg)
                {
                    AppLog.Info("Run", $"开始采集 {tp.Name}-{pp.Name} USG（全部工位，手动批量逻辑）");
                    await SamplePressurePointByValveGroupAsync(ctx, tp, pp, false, ct);
                    AppLog.Info("Run", $"采集完成 {tp.Name}-{pp.Name} USG");
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name}-{pp.Name} USG 采集（用户选择）");
                }

                TestCheckpoint.Save(ctx, ti, pi + 1, leakDone);
            }

            // ── 回差测量：下行 P100→P50→P0 ──
            if (!ctx.SkipUsg && ctx.Plan.PressurePoints.Count >= 2)
            {
                AppLog.Info("Run", $"开始回差测量（下行）：{tp.Name}");
                for (var pi = ctx.Plan.PressurePoints.Count - 2; pi >= 0; pi--)
                {
                    await ctx.WaitIfPausedAsync(ct);
                    var pp = ctx.Plan.PressurePoints[pi];
                    ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit} (回差)";
                    AppLog.Info("Run", $"回差下行 {tp.Name} {pp.Name}:{pp.Value} [{pp.PressureTypeDisplay}]");


                    AppLog.Info("Run", $"开始采集 {tp.Name}-{pp.Name} USG_R（回差，全部工位）");
                    await SamplePressurePointByValveGroupAsync(ctx, tp, pp, true, ct);
                    AppLog.Info("Run", $"采集完成 {tp.Name}-{pp.Name} USG_R（回差）");
                }
            }

            await ReadOvenTempForAllSlots(ctx, tp, ct);
            TestCheckpoint.Save(ctx, ti + 1, 0, leakDone);
        }

        AppLog.Info("Run", "完成采集");
        var metricCount = MetricsCalculator.Calculate(ctx);
        AppLog.Info("Cal", $"Metrics calculation complete for {metricCount} slots");
        SaveMatrix(ctx);
        TestCheckpoint.Delete();
        ctx.ResumeCheckpoint = null;

        if (ctx.Pressure is not null)
        {
            await OpenAllPressureValvesAsync(ctx, "Run", ct);
            await ctx.Pressure.VentAsync(ct);
            await CloseAllPressureValvesAsync(ctx, "Run", ct);
            AppLog.Info("Run", "泄压完成");
        }
        if (ctx.Oven is not null)
        {
            await ctx.Oven.StopAsync(ct);
            AppLog.Info("Run", "烘箱已停止");
        }
    }

    // ── 初始化设备：测试本轮用到的设备是否可连接 ───────────────────────────
    internal static async Task InitDevicesAsync(TaskContext ctx, CancellationToken ct)
    {
        AppLog.Info("Init", "正在初始化本轮测试需要的设备…");

        if (ctx.Pressure is not null)
        {
            try
            {
                if (!await ctx.Pressure.OpenAsync(ct))
                    throw new InvalidOperationException($"{ctx.Pressure.Model}@{ctx.Pressure.Address} 未连接");
                await ctx.Pressure.SetMeasureAsync(ct);
                AppLog.Info("Init", $"压力控制器 {ctx.Pressure.Model} 已连接");
            }
            catch (Exception ex) { throw new InvalidOperationException($"压力控制器初始化失败：{ex.Message}", ex); }
        }

        if (ctx.Oven is not null)
        {
            try
            {
                if (!await ctx.Oven.OpenAsync(ct))
                    throw new InvalidOperationException($"{ctx.Oven.Model}@{ctx.Oven.Address} 未连接");
                AppLog.Info("Init", $"烘箱 {ctx.Oven.Model} 已连接");
            }
            catch (Exception ex) { throw new InvalidOperationException($"烘箱初始化失败：{ex.Message}", ex); }
        }

        if (ctx.Dmm is not null)
        {
            try
            {
                if (!await ctx.Dmm.OpenAsync(ct))
                    throw new InvalidOperationException($"{ctx.Dmm.Model}@{ctx.Dmm.Address} 未连接");
                AppLog.Info("Init", $"DMM {ctx.Dmm.Model} 已连接");
            }
            catch (Exception ex) { throw new InvalidOperationException($"DMM 初始化失败：{ex.Message}", ex); }
        }

        if (ctx.Dac is not null)
        {
            try
            {
                if (!await ctx.Dac.OpenAsync(ct))
                    throw new InvalidOperationException($"{ctx.Dac.Model}@{ctx.Dac.Address} 未连接");
                AppLog.Info("Init", $"采集板 {ctx.Dac.Model} 已连接");
            }
            catch (Exception ex) { throw new InvalidOperationException($"采集板初始化失败：{ex.Message}", ex); }
        }

        if (ctx.Board is not null)
        {
            try
            {
                // Board 和 Dac 可能共享串口
                var sharesPort = ctx.Dac is not null &&
                    string.Equals(ctx.Board.Address, ctx.Dac.Address, StringComparison.OrdinalIgnoreCase);
                if (!sharesPort)
                {
                    if (!await ctx.Board.OpenAsync(ct))
                        throw new InvalidOperationException($"{ctx.Board.Model}@{ctx.Board.Address} 未连接");
                }
                AppLog.Info("Init", $"板卡 {ctx.Board.Model} 已连接");
            }
            catch (Exception ex) { throw new InvalidOperationException($"板卡初始化失败：{ex.Message}", ex); }
        }

        AppLog.Info("Init", "设备初始化完成");
    }

    // ── 探漏：逐阀加压检查泄漏率 ─────────────────────────────────────────
    private static List<ValveGroup> GetValveGroups(TaskContext ctx)
    {
        if (ctx.Dmm is null) return new();

        return ctx.Slots.Entries
            .GroupBy(slot => ResolveValveNo(slot))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderBy(g => int.TryParse(g.Key, out var n) ? n : int.MaxValue)
            .Select(g => new ValveGroup(
                ValveRelay.ForValveNo(ctx.Settings, g.Key),
                g.OrderBy(s => SlotDacAddress.ParseSlotIndex(s.Slot)).ToList()))
            .Where(g => g.Relay.IsValid)
            .ToList();
    }

    /// <summary>
    /// 工位 → 阀门号。优先取工位表里的「阀位」列（预览时已按测试工位算好），
    /// 缺失时按工位号推导（每 32 工位一个阀），与 SlotLayoutHelper.ValveForSlot 同源。
    /// </summary>
    private static string ResolveValveNo(SlotEntry slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.Valve) &&
            int.TryParse(slot.Valve.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared) &&
            declared > 0)
            return declared.ToString(CultureInfo.InvariantCulture);

        var slotIndex = SlotDacAddress.ParseSlotIndex(slot.Slot);
        if (slotIndex <= 0) return "";
        return SlotLayoutHelper.ValveForSlot(slotIndex).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task PreparePressureValveRoutingAsync(TaskContext ctx, CancellationToken ct)
    {
        var dmm = ctx.Dmm;
        if (dmm is null) return;

        var groups = GetValveGroups(ctx);
        if (groups.Count == 0) return;

        var masterValve = ValveRelay.Master(ctx.Settings);
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);
        var ventWaitMs = Math.Max(switchMs, GetDelayMs(ctx, "VentWaitMs", 120000) / 10);

        AppLog.Info("Valve", "开始加压测试前先打开全部阀门排空气路");
        if (masterValve.IsValid)
            await masterValve.OpenAsync(dmm, ct);
        foreach (var group in groups)
            await group.OpenAsync(ctx, ct);
        await Task.Delay(switchMs, ct);

        if (ctx.Pressure is not null)
        {
            await ctx.Pressure.VentAsync(ct);
            await Task.Delay(ventWaitMs, ct);
        }

        if (masterValve.IsValid)
            await masterValve.CloseAsync(dmm, ct);
        AppLog.Info("Valve", $"排空完成，已关闭总阀 {masterValve.Address}");
    }

    private static async Task ActivateValveGroupAsync(TaskContext ctx, ValveGroup targetGroup, string source, CancellationToken ct)
    {
        var dmm = ctx.Dmm;
        if (dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ValveRelay.Master(ctx.Settings);
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        try
        {
            if (masterValve.IsValid)
                await masterValve.CloseAsync(dmm, ct);
            foreach (var group in groups)
                await group.CloseAsync(ctx, ct);

            await targetGroup.OpenAsync(ctx, ct);
            await Task.Delay(switchMs, ct);
            AppLog.Info(source,
                $"阀门切换完成：关闭总阀 {masterValve.Address}，只打开 {targetGroup.ValveNo} 号阀 {targetGroup.Address}" +
                (targetGroup.Inverted ? "（该阀接线反相，实际下发 ROUT:CLOSE）" : ""));
        }
        catch (Exception ex)
        {
            AppLog.Error("Valve",
                $"阀门切换失败：目标阀{targetGroup.ValveNo} 地址{targetGroup.Address}，" +
                $"总阀{masterValve.Address}，{ex}");
            throw;
        }
    }

    private static async Task OpenAllPressureValvesAsync(TaskContext ctx, string source, CancellationToken ct)
    {
        var dmm = ctx.Dmm;
        if (dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ValveRelay.Master(ctx.Settings);
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        if (masterValve.IsValid)
            await masterValve.OpenAsync(dmm, ct);
        foreach (var group in groups)
            await group.OpenAsync(ctx, ct);
        await Task.Delay(switchMs, ct);
        AppLog.Info(source, "已打开全部阀门用于泄压/排空");
    }

    private static async Task CloseAllPressureValvesAsync(TaskContext ctx, string source, CancellationToken ct)
    {
        var dmm = ctx.Dmm;
        if (dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ValveRelay.Master(ctx.Settings);
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        if (masterValve.IsValid)
            await masterValve.CloseAsync(dmm, ct);
        foreach (var group in groups)
            await group.CloseAsync(ctx, ct);
        await Task.Delay(switchMs, ct);
        AppLog.Info(source, "已关闭全部阀门");
    }

    private static async Task SamplePressurePointByValveGroupAsync(
        TaskContext ctx,
        TempPoint tp,
        PressurePoint pp,
        bool reverse,
        CancellationToken ct)
    {
        var groups = GetValveGroups(ctx);
        var column = reverse ? $"{tp.Name}{pp.Name}_USG_R" : $"{tp.Name}{pp.Name}_USG";

        if (groups.Count == 0 || ctx.Dmm is null)
        {
            if (ctx.Pressure is not null)
                await SetAndWaitPressureAsync(ctx, pp, ct);
            await PressureHoldAsync(ctx, pp, ct);
            await DacBatchSampler.SampleAllAsync(
                ctx,
                DacMeasureKind.Usig,
                column,
                pp.Value,
                tp.Celsius,
                ct,
                beforeSlotAsync: (sampleIndex, token) =>
                    MonitorPressureForSamplingAsync(ctx, "全工位", pp, sampleIndex, token),
                beforeSlotEvery: GetIntSetting(ctx, "PressureMonitorEverySlots", 4));
            return;
        }

        foreach (var group in groups)
        {
            await SamplePressureGroupWithRecoveryAsync(ctx, group, tp, pp, column, ct);
        }
    }

    private static async Task SamplePressureGroupWithRecoveryAsync(
        TaskContext ctx,
        ValveGroup group,
        TempPoint tp,
        PressurePoint pp,
        string column,
        CancellationToken ct)
    {
        var configuredAttempts = GetIntSetting(ctx, "PressureGroupRetryCount", 2);
        var maxAttempts = Math.Max(1, configuredAttempts);
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                AppLog.Info("Pressure",
                    $"阀{group.ValveNo} {pp.Name} 开始稳压/采集，第{attempt}/{maxAttempts}次");
                await ctx.WaitIfPausedAsync(ct);
                await ActivateValveGroupAsync(ctx, group, "Run", ct);
                await SetAndWaitPressureAsync(ctx, pp, ct);
                await PressureHoldAsync(ctx, pp, ct);

                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit} / 阀{group.ValveNo}";
                var monitorEvery = GetIntSetting(ctx, "PressureMonitorEverySlots", 4);
                await DacBatchSampler.SampleAllAsync(
                    ctx,
                    DacMeasureKind.Usig,
                    column,
                    pp.Value,
                    tp.Celsius,
                    ct,
                    slotsOverride: group.Slots,
                    beforeSlotAsync: (sampleIndex, token) =>
                        MonitorPressureForSamplingAsync(ctx, $"阀{group.ValveNo}", pp, sampleIndex, token),
                    beforeSlotEvery: monitorEvery);

                AppLog.Info("Pressure",
                    $"阀{group.ValveNo} {pp.Name} 稳压采集完成，第{attempt}次通过");
                return;
            }
            catch (PressureStabilityException ex)
            {
                AppLog.Warn("Pressure",
                    $"阀{group.ValveNo} {pp.Name} 第{attempt}次稳压/采集失败：{ex.Message}");

                if (attempt < maxAttempts)
                {
                    AppLog.Warn("Pressure",
                        $"阀{group.ValveNo} 将重新切换阀门并重新稳压后重试");
                    continue;
                }

                var message =
                    $"阀{group.ValveNo}（{group.Address}）在 {tp.Name}-{pp.Name} 压力点持续波动。\n" +
                    $"目标压力：{pp.Value:G6}{ctx.Plan.PressureUnit}\n" +
                    $"{ex.Message}\n\n" +
                    "程序已自动重试，但仍未达到稳定条件。\n" +
                    "选择“是”重试当前阀组，选择“否”停止测试。";
                var retry = await ctx.ConfirmPressureInstabilityAsyncCore(message, ct);
                if (retry)
                {
                    AppLog.Warn("Pressure",
                        $"用户确认重试阀{group.ValveNo} {pp.Name}，重新开始当前阀组采集");
                    attempt = 0;
                    continue;
                }

                AppLog.Error("Pressure",
                    $"用户拒绝重试，停止测试：阀{group.ValveNo} {pp.Name}，{ex}");
                throw;
            }
        }
    }

    private static async Task MonitorPressureForSamplingAsync(
        TaskContext ctx,
        string route,
        PressurePoint pp,
        int sampleIndex,
        CancellationToken ct)
    {
        if (ctx.Pressure is null) return;

        var tolerance = GetPressureTolerance(ctx);
        float current;
        try
        {
            current = await ctx.Pressure.ReadPressureAsync(ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("Pressure",
                $"采集中压力读取失败：{route} {pp.Name}，第{sampleIndex + 1}个采样点，{ex}");
            throw new PressureStabilityException(
                $"采集中压力读取失败（{route}，采样点#{sampleIndex + 1}）：{ex.Message}", ex);
        }

        var diff = Math.Abs(current - pp.Value);
        AppLog.Info("Pressure",
            $"采集中压力监测：{route} {pp.Name}，采样点#{sampleIndex + 1}，" +
            $"当前={current:F4}{ctx.Plan.PressureUnit}，目标={pp.Value:F4}，差值={diff:F4}");

        if (!float.IsFinite(current) || diff > tolerance)
        {
            var message =
                $"采集过程中压力波动：{route}，{pp.Name}，采样点#{sampleIndex + 1}，" +
                $"当前={current:F4}{ctx.Plan.PressureUnit}，目标={pp.Value:F4}，" +
                $"允许差值±{tolerance:F4}";
            AppLog.Warn("Pressure", message);
            throw new PressureStabilityException(message);
        }
    }

    internal static async Task PerformLeakCheckAsync(TaskContext ctx, CancellationToken ct)
    {
        if (ctx.SkipLeakCheck)
        {
            AppLog.Info("Leak", "已跳过探漏（用户选择）");
            return;
        }
        if (ctx.Pressure is null || ctx.Dmm is null)
        {
            AppLog.Warn("Leak", "压力控制器或DMM未连接，跳过探漏");
            return;
        }

        var valveGroups = GetValveGroups(ctx);
        if (valveGroups.Count == 0)
        {
            AppLog.Warn("Leak", "未找到需要探漏的阀组，跳过");
            return;
        }

        var masterValve = ValveRelay.Master(ctx.Settings);
        AppLog.Info("Leak", $"本次探漏阀组：{string.Join(", ", valveGroups.Select(v => $"阀{v.ValveNo}({v.Slots.Count}工位)"))}（共{valveGroups.Count}组）");

        var leakPressures = LeakCheckPlanHelper.ResolvePressures(ctx.Plan);
        if (leakPressures.Count == 0)
        {
            AppLog.Warn("Leak", "未配置探漏压力点且无法从方案推导，跳过探漏");
            return;
        }

        var leakPrecision = LeakCheckPlanHelper.ResolvePrecision(ctx.Plan);
        var pressureUnit = string.IsNullOrWhiteSpace(ctx.Plan.PressureUnit) ? "kPa" : ctx.Plan.PressureUnit;
        var leakPrecisionPa = PressureRateToPa(leakPrecision, pressureUnit);
        var leakCheckSec = 30;
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);
        var pressureType = ctx.Plan.DefaultPressureType;

        AppLog.Info("Leak",
            $"探漏参数：类型={pressureType}，压力点=[{string.Join(", ", leakPressures)}]{pressureUnit}，精度/泄漏率阈值={leakPrecisionPa:G4}Pa/s，观测={leakCheckSec}s");

        await ctx.Pressure.SetPressureTypeAsync(pressureType, ct);

        // 初始状态：全部阀门关闭（主阀始终保持关闭）
        AppLog.Info("Leak", "关闭所有阀门");
        if (masterValve.IsValid)
            await masterValve.CloseAsync(ctx.Dmm, ct);
        foreach (var group in valveGroups)
            await group.CloseAsync(ctx, ct);
        await Task.Delay(switchMs, ct);

        // 泄压
        AppLog.Info("Leak", "泄压");
        await ctx.Pressure.VentAsync(ct);
        await Task.Delay(GetDelayMs(ctx, "VentWaitMs", 120000) / 10, ct);

        var worstLeakRate = double.NegativeInfinity;
        var worstLeakLabel = "";
        var worstLeakPressure = 0f;
        var anyExceeded = false;
        // 执行异常的阀组 = "未检测"。历史缺陷：异常被吞后其余组通过即整体判"探漏通过"。
        var failedGroups = new List<string>();

        foreach (var leakP in leakPressures)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Info("Leak", $"── 探漏压力点 {leakP}{pressureUnit} ──");

            // 逐阀探漏：每次只开被测阀，其余全关，主阀不动
            foreach (var group in valveGroups)
            {
                ct.ThrowIfCancellationRequested();
                var vName = $"阀{group.ValveNo}";
                AppLog.Info("Leak", $"正在对{vName}进行探漏 @ {leakP}{pressureUnit}");
                try
                {
                    foreach (var g in valveGroups) await g.CloseAsync(ctx, ct);
                    await group.OpenAsync(ctx, ct);
                    await Task.Delay(switchMs, ct);

                    var (passed, leakRate) = await RunLeakRateCheckAsync(
                        ctx,
                        $"{vName} @ {leakP}{pressureUnit}",
                        leakP,
                        pressureUnit,
                        leakPrecision,
                        leakCheckSec,
                        15,
                        ct);

                    if (leakRate > worstLeakRate)
                    {
                        worstLeakRate = leakRate;
                        worstLeakLabel = $"{vName} @ {leakP}{pressureUnit}";
                        worstLeakPressure = leakP;
                    }

                    if (!passed)
                        anyExceeded = true;

                    if (!passed)
                        AppLog.Warn("Leak", $"{vName} @ {leakP}{pressureUnit} 探漏失败");

                    await group.CloseAsync(ctx, ct);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Leak", $"{vName} @ {leakP}{pressureUnit} 探漏失败: {ex.Message}");
                    failedGroups.Add($"{vName}@{leakP}{pressureUnit}");
                }
            }

        }

        // 泄压结束
        await ctx.Pressure.VentAsync(ct);

        var failedSummary = failedGroups.Count == 0 ? null : string.Join("、", failedGroups);
        if (failedSummary is not null)
            AppLog.Error("Leak", $"探漏异常：{failedGroups.Count} 组执行失败未能检测（{failedSummary}），需人工确认");

        if (worstLeakRate > double.NegativeInfinity)
        {
            var status = anyExceeded ? "超限" : "通过";
            var worstLeakRatePa = PressureRateToPa(worstLeakRate, pressureUnit);
            AppLog.Info("Leak", $"探漏完成，最高漏率 {worstLeakRatePa:G4}Pa/s（{worstLeakLabel}，{status}）");
            // 存在未完成检测的阀组时不得写"探漏通过"
            if (!anyExceeded && failedGroups.Count == 0)
                ctx.LeakCheckNote = $"探漏通过：最高漏率 {worstLeakRatePa:G4}Pa/s";
        }
        else if (failedGroups.Count == 0)
        {
            AppLog.Info("Leak", "探漏完成，已泄压，所有阀门已关闭");
            ctx.LeakCheckNote = "探漏通过";
        }

        if (anyExceeded || failedGroups.Count > 0)
        {
            var reason = anyExceeded
                ? $"探漏超限：{worstLeakLabel}\n最高漏率：{PressureRateToPa(worstLeakRate, pressureUnit):G4}Pa/s\n漏率指标：{leakPrecisionPa:G4}Pa/s"
                : "探漏执行异常：部分阀组未能完成检测";
            if (failedSummary is not null)
                reason += $"\n异常阀组（未检测）：{failedSummary}";

            var message = reason + "\n\n是否继续进行自动测试？\n选择“是”继续，选择“否”停止。";
            var continueTest = await ctx.ConfirmLeakCheckExceededAsyncCore(message, ct);
            if (continueTest)
            {
                AppLog.Warn("Leak", $"用户确认继续自动测试：{(anyExceeded ? worstLeakLabel : "存在未完成检测的阀组")}");
                ctx.LeakCheckNote = failedSummary is null
                    ? $"探漏超限，人工放行：{worstLeakLabel}"
                    : $"探漏异常，人工放行：未检测 [{failedSummary}]";
                return;
            }

            throw new InvalidOperationException(reason.Replace("\n", "，"));
        }
    }

    private static async Task<(bool Passed, double LeakRate)> RunLeakRateCheckAsync(
        TaskContext ctx,
        string label,
        float targetPressure,
        string unit,
        float precision,
        int observeSec,
        int stabilizeSec,
        CancellationToken ct)
    {
        AppLog.Info("Leak", $"{label} 加压 {targetPressure}{unit} 开始");
        await ctx.Pressure!.SetPressureAsync(targetPressure, unit, precision, ct);
        await Task.Delay(stabilizeSec * 1000, ct);

        await ctx.Pressure.SetMeasureAsync(ct);
        AppLog.Info("Leak", $"{label} switched to measure mode before leak-rate reading");

        AppLog.Info("Leak", $"{label} 检查泄漏率（观测 {observeSec}s）");
        var p1 = await ctx.Pressure.ReadPressureAsync(ct);
        await Task.Delay(observeSec * 1000, ct);
        ct.ThrowIfCancellationRequested();
        var p2 = await ctx.Pressure.ReadPressureAsync(ct);
        var leakRate = Math.Abs(p2 - p1) / observeSec;

        if (leakRate < precision)
        {
            AppLog.Info("Leak", $"{label} 探漏通过，泄漏率={PressureRateToPa(leakRate, unit):G4}Pa/s");
            return (true, leakRate);
        }

        AppLog.Warn("Leak", $"{label} 探漏失败，泄漏率={PressureRateToPa(leakRate, unit):G4}Pa/s（阈值<{PressureRateToPa(precision, unit):G4}Pa/s）");
        return (false, leakRate);
    }

    // ── 设置烘箱并等待温度到达（1分钟间隔日志，最长240分钟）───────────────
    internal static async Task SetAndWaitOvenAsync(TaskContext ctx, TempPoint tp, string source, CancellationToken ct)
    {
        try
        {
            AppLog.Info(source, $"设置烘箱温度至{tp.Celsius}度");
            var ok = await ctx.Oven!.SetTempAsync(tp.Celsius, ct);
            if (!ok)
            {
                throw new InvalidOperationException("设置烘箱温度或启动加热失败（TEMP/POWER 指令未返回 OK）");
            }
            AppLog.Info(source, "烘箱已发送加热指令（TEMP + POWER,ON）");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"设置烘箱温度失败：{ex.Message}", ex);
        }

        var maxMinutes = int.TryParse(ctx.Settings.Get("DelaySettings", "TempReachTimeoutMin", "240"), out var m) ? m : 240;
        AppLog.Info(source, $"开始加热，最大加热时间 {maxMinutes} min");
        int nanCount = 0;

        for (var min = 0; min <= maxMinutes; min++)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            try
            {
                var current = await ctx.Oven.ReadTempAsync(ct);
                if (float.IsNaN(current))
                {
                    nanCount++;
                    ctx.CurrentTemp = $"{tp.Name} 目标{tp.Celsius}°C 读取失败(NaN) 已加热{min}min";
                    AppLog.Warn(source, $"目标温度为 {tp.Celsius}°C;  烘箱已加热 {min} min; 当前温度为 NaN°C（通讯异常）");
                    if (nanCount >= 3)
                    {
                        throw new InvalidOperationException("连续3次读取烘箱温度失败，停止本轮测试");
                    }
                }
                else
                {
                    nanCount = 0;
                    ctx.CurrentTemp = $"{tp.Name} 目标{tp.Celsius}°C 当前{current:F1}°C 已加热{min}min";
                    AppLog.Info(source, $"目标温度为 {tp.Celsius}°C;  烘箱已加热 {min} min; 当前温度为 {current:F1}°C ");
                    if (Math.Abs(current - tp.Celsius) <= 1.0f)
                    {
                        AppLog.Info(source, "加热完成");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(source, $"读取烘箱温度失败: {ex.Message}");
                nanCount++;
                if (nanCount >= 3)
                {
                    throw new InvalidOperationException("连续3次读取烘箱温度失败，停止本轮测试", ex);
                }
            }
            if (min < maxMinutes)
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
        throw new TimeoutException($"烘箱加热超时（{maxMinutes} min），未达到 {tp.Celsius}℃");
    }

    // ── 保温（1分钟间隔日志，显示已保温/剩余）────────────────────────────
    internal static async Task SoakWithLogAsync(TaskContext ctx, int totalMinutes, string tpName, CancellationToken ct)
    {
        for (var min = 0; min <= totalMinutes; min++)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            var remaining = totalMinutes - min;
            ctx.CurrentTemp = $"{tpName} 保温中 {min}/{totalMinutes} min (剩余{remaining}min)";
            AppLog.Info("Run", $"烘箱已保温 {min} min, 还剩 {remaining} min");
            if (min < totalMinutes)
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    // ── 设置压力并连续确认稳定 ───────────────────────────────────────────
    internal static async Task SetAndWaitPressureAsync(TaskContext ctx, PressurePoint pp, CancellationToken ct)
    {
        if (ctx.Pressure is null)
        {
            AppLog.Info("Run", $"跳过压力设置：压力控制器未启用（{pp.Name}={pp.Value}{ctx.Plan.PressureUnit}）");
            return;
        }

        var tolerance = GetPressureTolerance(ctx);
        var requiredSamples = Math.Max(2, GetIntSetting(ctx, "PressureStableSamples", 5));
        var sampleMs = Math.Max(100, GetIntSetting(ctx, "PressureStableSampleMs", 500));
        var timeoutMs = Math.Max(sampleMs * requiredSamples, GetIntSetting(ctx, "PressureStableTimeoutMs", 120000));
        var maxAttempts = Math.Max(1, GetIntSetting(ctx, "PressureStabilityRetryCount", 3));
        var lastError = "";

        // 自动切换压力类型（绝压/表压/差压）
        await ctx.Pressure.SetPressureTypeAsync(pp.PressureType, ct);
        AppLog.Info("Pressure",
            $"压力类型切换为 {pp.PressureType} ({pp.PressureTypeDisplay})，" +
            $"稳定条件：连续{requiredSamples}次、间隔{sampleMs}ms、允许差值±{tolerance:G6}{ctx.Plan.PressureUnit}");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ctx.Pressure.SetPressureAsync(pp.Value, ctx.Plan.PressureUnit, ctx.Plan.Precision, ct);
                AppLog.Info("Pressure",
                    $"{pp.Name} 第{attempt}/{maxAttempts}次设定压力：目标={pp.Value:G6}{ctx.Plan.PressureUnit}");

                var stableSamples = 0;
                var readCount = 0;
                var readErrors = 0;
                var min = double.PositiveInfinity;
                var max = double.NegativeInfinity;
                var started = DateTime.UtcNow;

                while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    await ctx.WaitIfPausedAsync(ct);
                    readCount++;

                    float current;
                    try
                    {
                        current = await ctx.Pressure.ReadPressureAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        readErrors++;
                        stableSamples = 0;
                        lastError = ex.Message;
                        AppLog.Error("Pressure",
                            $"{pp.Name} 第{attempt}次稳压读取失败（第{readCount}次）：{ex}");
                        await Task.Delay(sampleMs, ct);
                        continue;
                    }

                    if (!float.IsFinite(current))
                    {
                        stableSamples = 0;
                        lastError = $"压力控制器返回无效值 {current}";
                        AppLog.Error("Pressure",
                            $"{pp.Name} 第{attempt}次稳压读取到无效值：{current}");
                        await Task.Delay(sampleMs, ct);
                        continue;
                    }

                    var diff = Math.Abs(current - pp.Value);
                    min = Math.Min(min, current);
                    max = Math.Max(max, current);
                    ctx.CurrentPressure =
                        $"{pp.Name} 目标{pp.Value:F4} 当前{current:F4} 差值{diff:F4} {ctx.Plan.PressureUnit} " +
                        $"稳定计数{stableSamples}/{requiredSamples}";

                    if (diff <= tolerance)
                    {
                        stableSamples++;
                    }
                    else
                    {
                        if (stableSamples > 0)
                        {
                            AppLog.Warn("Pressure",
                                $"{pp.Name} 稳定计数被打断：当前={current:F4}，目标={pp.Value:F4}，" +
                                $"差值={diff:F4}，允许±{tolerance:F4}");
                        }
                        stableSamples = 0;
                    }

                    if (readCount == 1 || readCount % 10 == 0 || stableSamples == requiredSamples)
                    {
                        AppLog.Info("Pressure",
                            $"{pp.Name} 稳压监测：目标={pp.Value:F4} 当前={current:F4} 差值={diff:F4} " +
                            $"({stableSamples}/{requiredSamples})，读取#{readCount}");
                    }

                    if (stableSamples >= requiredSamples)
                    {
                        AppLog.Info("Pressure",
                            $"{pp.Name} 连续稳定通过：当前={current:F4}{ctx.Plan.PressureUnit}，" +
                            $"连续样本={requiredSamples}，监测范围={min:F4}~{max:F4}，" +
                            $"读取错误={readErrors}");
                        return;
                    }

                    await Task.Delay(sampleMs, ct);
                }

                lastError =
                    $"稳定超时：读取{readCount}次，读取错误{readErrors}次，" +
                    $"监测范围={(double.IsInfinity(min) ? "无有效值" : $"{min:F4}~{max:F4}")}";
                AppLog.Warn("Pressure",
                    $"{pp.Name} 第{attempt}/{maxAttempts}次{lastError}，准备重新设压");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                AppLog.Error("Pressure",
                    $"{pp.Name} 第{attempt}/{maxAttempts}次设压/稳压异常：{ex}");
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(GetDelayMs(ctx, "PressureRecoveryWaitMs", 1000), ct);
                AppLog.Warn("Pressure",
                    $"{pp.Name} 自动重新稳压：第{attempt + 1}/{maxAttempts}次");
            }
        }

        var finalMessage =
            $"压力点 {pp.Name} 无法稳定：目标 {pp.Value:G6}{ctx.Plan.PressureUnit}，" +
            $"允许差值±{tolerance:G6}，已重试{maxAttempts}次。{lastError}";
        AppLog.Error("Pressure", finalMessage);
        throw new PressureStabilityException(finalMessage);
    }

    /// <summary>保压期间持续读取压力，发现波动时自动重新稳压。</summary>
    internal static async Task PressureHoldAsync(TaskContext ctx, PressurePoint pp, CancellationToken ct)
    {
        if (ctx.Pressure is null)
        {
            AppLog.Info("Run", $"跳过保压：压力控制器未启用（{pp.Name}={pp.Value}{ctx.Plan.PressureUnit}）");
            return;
        }

        var holdMs = GetDelayMs(ctx, "PressureAfterMs", 60000);
        if (holdMs <= 0) return;

        var monitorMs = Math.Max(250, GetIntSetting(ctx, "PressureMonitorIntervalMs", 1000));
        var tolerance = GetPressureTolerance(ctx);
        var holdSec = holdMs / 1000;
        var monitorCount = 0;
        var recoveryCount = 0;
        var started = DateTime.UtcNow;
        AppLog.Info("Pressure",
            $"{pp.Value}{ctx.Plan.PressureUnit} 开始保压 {holdSec}s，" +
            $"监测间隔={monitorMs}ms，允许差值±{tolerance:G6}");

        while ((DateTime.UtcNow - started).TotalMilliseconds < holdMs)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            monitorCount++;
            var current = float.NaN;
            try
            {
                current = await ctx.Pressure.ReadPressureAsync(ct);
                if (!float.IsFinite(current))
                    throw new InvalidOperationException($"压力控制器返回无效值 {current}");

                var diff = Math.Abs(current - pp.Value);
                ctx.CurrentPressure =
                    $"{pp.Name} {pp.Value}{ctx.Plan.PressureUnit} 保压中 " +
                    $"{(int)(DateTime.UtcNow - started).TotalSeconds}/{holdSec}s，当前{current:F4}";

                if (diff > tolerance)
                {
                    recoveryCount++;
                    AppLog.Warn("Pressure",
                        $"{pp.Name} 保压期间发现压力波动：当前={current:F4}，" +
                        $"目标={pp.Value:F4}，差值={diff:F4}，允许±{tolerance:F4}，" +
                        $"第{recoveryCount}次自动恢复");
                    await SetAndWaitPressureAsync(ctx, pp, ct);
                    AppLog.Info("Pressure", $"{pp.Name} 保压压力已重新稳定，继续保压");
                }
                else if (monitorCount == 1 || monitorCount % 10 == 0)
                {
                    AppLog.Info("Pressure",
                        $"{pp.Name} 保压监测正常：当前={current:F4}，差值={diff:F4}，" +
                        $"进度={(int)(DateTime.UtcNow - started).TotalSeconds}/{holdSec}s");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PressureStabilityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("Pressure",
                    $"{pp.Name} 保压期间压力读取/恢复失败：当前={current:F4}，{ex}");
                throw new PressureStabilityException(
                    $"{pp.Name} 保压期间压力读取/恢复失败：{ex.Message}", ex);
            }

            var remaining = holdMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (remaining > 0)
                await Task.Delay(Math.Min(monitorMs, remaining), ct);
        }

        AppLog.Info("Pressure",
            $"{pp.Value}{ctx.Plan.PressureUnit} 保压完成，监测次数={monitorCount}，自动恢复={recoveryCount}");
    }

    // ── 读取烘箱实际温度，写入所有工位（每温度点采集完成后调用一次）─────────
    internal static async Task ReadOvenTempForAllSlots(TaskContext ctx, TempPoint tp, CancellationToken ct)
    {
        if (ctx.SkipOvenTemp)
        {
            AppLog.Info("Run", $"跳过 {tp.Name} 烘箱温度采集（用户选择）");
            return;
        }

        var col = $"{tp.Name}_OvenTemp";
        if (!ctx.Columns.Contains(col)) ctx.Columns.Add(col);

        double ovenTemp = double.NaN;
        if (ctx.Oven is not null)
        {
            try
            {
                ovenTemp = await ctx.Oven.ReadTempAsync(ct);
                AppLog.Info("Read", $"{tp.Name} 烘箱实际温度：{ovenTemp:F5}℃");
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"{tp.Name} 读取烘箱温度失败：{ex.Message}");
            }
        }

        // 所有工位写入同一个烘箱温度值
        foreach (var slot in ctx.Slots.Entries)
        {
            ctx.Matrix.Set(slot.Slot, col, ovenTemp, double.IsNaN(ovenTemp) ? CellStatus.Error : CellStatus.Ok);
        }
    }

    private static int GetDelayMs(TaskContext ctx, string key, int fallback) =>
        GetIntSetting(ctx, key, fallback);

    private static int GetIntSetting(TaskContext ctx, string key, int fallback)
    {
        var text = ctx.Settings.Get(
            "DelaySettings",
            key,
            fallback.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : fallback;
    }

    private static double GetPressureTolerance(TaskContext ctx)
    {
        var configured = ctx.Settings.Get("DelaySettings", "PressureStabilityTolerance", "");
        if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            value > 0 &&
            double.IsFinite(value))
            return value;

        return Math.Max(ctx.Plan.Precision, 0.01f);
    }

    private static double PressureRateToPa(double value, string unit) =>
        unit.Trim().ToUpperInvariant() switch
        {
            "MPA" => value * 1_000_000.0,
            "KPA" => value * 1_000.0,
            "PA" => value,
            _ => value
        };

    public static void SaveMatrix(TaskContext ctx)
    {
        SaveMatrixWithCurrentLayout(ctx);
    }

    private static void SaveMatrixWithCurrentLayout(TaskContext ctx)
    {
        var sensor = string.IsNullOrWhiteSpace(ctx.Plan.SensorType) ? ctx.Plan.Name : ctx.Plan.SensorType;
        var safeSensor = SafePath(sensor);
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyyMMdd");
        var timeStr = now.ToString("HHmmss");

        var sensorDir = Path.Combine(AppPaths.DataDir, safeSensor);
        Directory.CreateDirectory(sensorDir);

        var existing = Directory.GetDirectories(sensorDir, $"{safeSensor}-{now:yyMMdd}*");
        var seq = existing.Length + 1;
        var batchNameBase = $"{safeSensor}-{now:yyMMddHH_mm}";
        var batchName = batchNameBase;
        var duplicate = 2;
        while (Directory.Exists(Path.Combine(sensorDir, batchName)))
            batchName = $"{batchNameBase}-{duplicate++:D2}";
        var batchDir = Path.Combine(sensorDir, batchName);
        Directory.CreateDirectory(batchDir);

        var serialMap = ctx.Slots.Entries.ToDictionary(s => s.Slot, s => s.SerialNo);

        // 报表模板由运行设置窗口「生成模板」决定：
        //   Auto  = 自动测试模板，沿用现场既有的全性能.xlsx，桌面仍写旧版兼容 CSV（行为不变）；
        //   Wafer = 晶圆测试模板，用「生成的数据格式」下的样例产出报表，并在桌面另存一份同名 xlsx。
        var templateMode = TemplatePerformanceExporter.ResolveTemplateMode(ctx.Settings);
        var waferMode = templateMode == ReportTemplateMode.Wafer;

        if (LegacyCsvExporter.IsLegacyProfile(ctx.Plan))
        {
            // 现场报表命名与样例一致：日期 时间-序号 型号(性能测试).xlsx，
            // 型号优先取运行设置窗口录入的报表型号，留空回落方案型号（去掉 M30- 前缀）。
            var templateXlsxName = $"{dateStr} {timeStr}-{seq:D2} {TemplatePerformanceExporter.ResolveModelName(ctx)}(性能测试).xlsx";
            var templateXlsx = Path.Combine(batchDir, templateXlsxName);
            try
            {
                TemplatePerformanceExporter.Export(ctx, templateXlsx,
                    TemplatePerformanceExporter.ResolveTemplatePath(templateMode));
                AppLog.Info("Save", $"性能测试报表保存到 {templateXlsx}");
            }
            catch (Exception ex)
            {
                // 保留旧导出作为兜底，避免现场没有随程序部署模板时丢失数据。
                AppLog.Warn("Save", $"性能测试报表导出失败，改用通用 XLSX：{ex.Message}");
                ctx.Matrix.ExportXlsx(templateXlsx, ctx.Columns, serialMap);
                AppLog.Info("Save", $"通用 XLSX 数据保存到 {templateXlsx}");
            }

            if (waferMode)
            {
                CopyReportToDesktop(templateXlsx, templateXlsxName);
                return;
            }

            try
            {
                var csvName = $"{dateStr} {timeStr}-{seq:D2} {safeSensor}(性能测试).csv";
                var csv = Path.Combine(batchDir, csvName);
                ctx.Matrix.ExportCsv(csv, ctx.Columns, serialMap);
                AppLog.Info("Save", $"兼容 CSV 数据保存到 {csv}");
                LegacyCsvExporter.Export(ctx);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Save", $"旧版格式 CSV 导出失败: {ex.Message}");
            }
            return;
        }

        var xlsxName = $"{dateStr} {timeStr}-{seq:D2} {safeSensor}(性能测试).xlsx";
        var xlsx = Path.Combine(batchDir, xlsxName);
        ctx.Matrix.ExportXlsx(xlsx, ctx.Columns, serialMap);
        AppLog.Info("Save", $"数据保存到 {xlsx}");

        if (waferMode)
            CopyReportToDesktop(xlsx, xlsxName);
    }

    /// <summary>晶圆测试模式：把生成的报表在桌面另存一份（同名覆盖），方便现场直接取用。</summary>
    private static void CopyReportToDesktop(string source, string fileName)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) return;

            var target = Path.Combine(desktop, fileName);
            File.Copy(source, target, overwrite: true);
            AppLog.Info("Save", $"报表已在桌面另存一份：{target}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Save", $"桌面另存报表失败: {ex.Message}");
        }
    }

    private static string SafePath(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "未命名方案" : safe;
    }
}

/// <summary>
/// 万用表自动测试（Run:DmmAutoTest）——与电路板自动测试并列的采集方案。
///
/// 用 1-3 台 DAQ973A 采电压/电阻，最多 48 工位：
///   工位 1-16 → 973-1，17-32 → 973-2，33-48 → 973-3（每台独立的通道组）
///   电压通道 101-116、电阻通道 201-216、工位电源开关 301-316、总电源 319/320
///
/// 每个温度点的采集顺序：
///   1. 上电：每台 973 闭合本台 301-316 + 319/320（总电源同时闭合）
///   2. 逐压力点（正向）：设压力 → 稳定 → 保压 → 逐工位读电压 mV → {Tn}{Pm}_USG
///   3. 下行回差（压力点 ≥2）：反向再走一遍 → {Tn}{Pm}_USG_R
///   4. 断电：每台 973 断开 301-320
///   5. 逐工位读电阻 Ω → {Tn}_DMM_R
///   6. 读烘箱温度 → {Tn}_OvenTemp
///
/// 控制对象是传感器电源开关；全程不触碰气路阀门（<see cref="ValveRelay"/>）——那是电路板测试的东西。
/// </summary>
public sealed class RunDmmAutoTestAction : IAction
{
    public string Key => "Run:DmmAutoTest";

    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Dmms.Count == 0 && ctx.Dmm is null)
        {
            AppLog.Error("Run", "万用表自动测试未配置 DAQ973A，已中止");
            throw new InvalidOperationException("DmmAutoTest requires at least one DAQ973A");
        }

        ctx.Matrix.Clear();
        ctx.Columns.Clear();
        foreach (var slot in ctx.Slots.Entries)
            ctx.Matrix.EnsureSlot(slot.Slot);

        var units = ActiveUnits(ctx);
        EnsureUnitsAvailable(ctx, units);
        var powerMap = PowerSwitchMap(ctx);

        AppLog.Info("Run",
            $"开始万用表自动测试：方案={ctx.Plan.Name}，工位={ctx.Slots.Entries.Count}，DAQ973A×{units.Count}，" +
            $"控制对象=传感器电源开关（不控制气路阀门）");

        await OpenDmmsAsync(ctx, ct);

        // 探漏会关总阀 + 各阀组保压，属于电路板测试的气路逻辑。
        // 万用表模式只控传感器电源开关、不控任何气路阀门，因此一律不跑探漏：
        // 工位表里若残留阀门列（旧工位表或手工改过 ini），直接执行会误关现场气路。
        AppLog.Info("Leak",
            "万用表采集模式不控制气路阀门，跳过探漏（需要气密性检测请用电路板采集模式）");

        var powered = false;
        try
        {
            for (var ti = 0; ti < ctx.Plan.TempPoints.Count; ti++)
            {
                await ctx.WaitIfPausedAsync(ct);
                var tp = ctx.Plan.TempPoints[ti];
                ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";
                ctx.CurrentPressure = "";
                AppLog.Info("Run", $"万用表自动测试：温度点 {tp.Name} ({ti + 1}/{ctx.Plan.TempPoints.Count})");

                if (ctx.Oven is not null && ctx.Oven.State == ConnectionState.Connected)
                {
                    await RunPerformanceTestAction.SetAndWaitOvenAsync(ctx, tp, "Run", ct);
                    var soak = tp.SoakMinutes ?? (int.TryParse(ctx.Settings.Get("DelaySettings", "SoakMinutes", "120"), out var sm) ? sm : 120);
                    await RunPerformanceTestAction.SoakWithLogAsync(ctx, soak, tp.Name, ct);
                }
                else
                {
                    AppLog.Info("Run", "烘箱未连接或未启用，跳过温度设置和保温");
                }

                // 电压：需要上电 → 逐压力点采集 → 断电
                if (!ctx.SkipDmmVoltage)
                {
                    await PowerOnAsync(ctx, tp, powerMap, ct);
                    powered = true;
                    try
                    {
                        await SweepPressureAsync(ctx, tp, reverse: false, ct);
                        if (ctx.Plan.PressurePoints.Count >= 2)
                            await SweepPressureAsync(ctx, tp, reverse: true, ct);
                    }
                    finally
                    {
                        await PowerOffAsync(ctx, tp, powerMap, ct);
                        powered = false;
                    }
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name} 电压采集（用户选择）");
                }

                // 电阻：断电状态下采集
                if (!ctx.SkipDmmResistance)
                    await ReadResistanceAsync(ctx, tp, ct);
                else
                    AppLog.Info("Run", $"跳过 {tp.Name} 电阻采集（用户选择）");

                await RunPerformanceTestAction.ReadOvenTempForAllSlots(ctx, tp, ct);
            }
        }
        finally
        {
            // 兜底：异常 / 取消路径上绝不能把 301-316、319-320 留在闭合状态。
            // 用 CancellationToken.None —— 原 token 可能已取消，断电必须继续做完。
            if (powered)
            {
                AppLog.Warn("Run", "流程提前退出，兜底断开全部工位电源与总电源");
                try { await PowerOffAsync(ctx, null, powerMap, CancellationToken.None); }
                catch (Exception ex) { AppLog.Error("Run", $"兜底断电失败：{ex.Message}"); }
            }
        }

        MetricsCalculator.Calculate(ctx);
        RunPerformanceTestAction.SaveMatrix(ctx);

        if (ctx.Pressure is not null)
            await ctx.Pressure.VentAsync(ct);
        if (ctx.Oven is not null)
            await ctx.Oven.StopAsync(ct);
    }

    // ─── 设备与通道解析 ─────────────────────────────────────────────────────

    /// <summary>本次工位表实际用到的 973 台号（0-based），按升序。</summary>
    private static List<int> ActiveUnits(TaskContext ctx) =>
        ctx.Slots.Entries
            .Select(s => DmmAutoSlotMap.UnitIndexForSlot(SlotDacAddress.ParseSlotIndex(s.Slot)))
            .Distinct()
            .OrderBy(u => u)
            .ToList();

    /// <summary>
    /// 工位数不得超过「已连接的 973 台数 × 每台工位数」，超了直接中止。
    ///
    /// 必须显式拦的原因：超限工位在 <see cref="DmmAutoSlotMap.UnitIndexForSlot"/> 里被 clamp 到最后一台，
    /// 而 <see cref="TaskContext.DmmAt"/> 越界又会回退主 DMM —— 两者叠加，工位 17（只配了 1 台时）
    /// 会去读 973-1 的通道 101，矩阵照样出数、状态还是 Ok，数据却串到了别的工位。
    /// 与其出脏数据不如当场中止，并提示怎么改。
    /// </summary>
    private static void EnsureUnitsAvailable(TaskContext ctx, List<int> units)
    {
        var connected = Math.Max(ctx.Dmms.Count, ctx.Dmm is null ? 0 : 1);
        var missing = units.Where(u => u >= connected).Distinct().OrderBy(u => u).ToList();
        if (missing.Count == 0)
            return;

        var names = string.Join("、", missing.Select(u => $"973-{u + 1}"));
        var limit = connected * DmmAutoSlotMap.SlotsPerUnit;
        AppLog.Error("Run",
            $"工位表需要 {names}，但只连接了 {connected} 台 DAQ973A（本轮上限 {limit} 个工位），已中止");
        throw new InvalidOperationException(
            $"工位表超出已连接的 DAQ973A 台数：工位表需要 {names}，实际只有 {connected} 台。" +
            $"请到「配置 → 设备」把 DAQ973A 台数调大，或减少工位数 / 调整起始工位（本轮上限 {limit} 个工位）。");
    }

    /// <summary>电压通道：优先用工位表「通道」列，缺省按工位号推导。</summary>
    private static string VoltageChannel(SlotEntry slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.Channel) && slot.Channel.Trim() != "-")
            return slot.Channel.Trim();
        var n = SlotDacAddress.ParseSlotIndex(slot.Slot);
        return DmmAutoSlotMap.VoltageChannelForSlot(n).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>电阻通道：优先用工位表推导值（表里没有独立列），按工位号算 201-216。</summary>
    private static string ResistanceChannel(SlotEntry slot) =>
        DmmAutoSlotMap.ResistanceChannelForSlot(SlotDacAddress.ParseSlotIndex(slot.Slot))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>电源开关通道：优先用「电源开关」列，缺省按工位号推导 301-316。</summary>
    private static string PowerSwitchChannel(SlotEntry slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.PowerSwitch) && slot.PowerSwitch.Trim() != "-")
            return slot.PowerSwitch.Trim();
        var n = SlotDacAddress.ParseSlotIndex(slot.Slot);
        return DmmAutoSlotMap.PowerSwitchChannelForSlot(n).ToString(CultureInfo.InvariantCulture);
    }

    private static int GetSlotDelayMs(TaskContext ctx) =>
        int.TryParse(ctx.Settings.Get("DelaySettings", "DmmSlotDelayMs", "100"), out var ms) ? Math.Max(0, ms) : 100;

    private static int GetSwitchDelayMs(TaskContext ctx) =>
        int.TryParse(ctx.Settings.Get("DelaySettings", "DmmSwitchMs", "500"), out var ms) ? Math.Max(0, ms) : 500;

    // ─── 设备初始化 ─────────────────────────────────────────────────────────

    private static async Task OpenDmmsAsync(TaskContext ctx, CancellationToken ct)
    {
        for (var i = 0; i < ctx.Dmms.Count; i++)
        {
            var dmm = ctx.Dmms[i];
            await dmm.OpenAsync(ct);
            if (dmm.State != ConnectionState.Connected)
                throw new InvalidOperationException($"DAQ973A #{i + 1} 连接失败（{dmm.Address}）");
            AppLog.Info("Init", $"DAQ973A #{i + 1} {dmm.Model} 已连接（{dmm.Address}）");
        }
    }

    // ─── 电源开关（973 通道三） ──────────────────────────────────────────────

    /// <summary>
    /// 本轮工位表用到的「973 台号 → 该台工位电源开关通道」映射（台号升序、每台通道去重）。
    /// 上电/断电各算一次即可 —— 原先每台 973 都把整张工位表重新过一遍。
    /// </summary>
    private static List<(int Unit, List<string> Channels)> PowerSwitchMap(TaskContext ctx) =>
        ctx.Slots.Entries
            .Select(s => (Unit: DmmAutoSlotMap.UnitIndexForSlot(SlotDacAddress.ParseSlotIndex(s.Slot)),
                          Channel: PowerSwitchChannel(s)))
            .GroupBy(x => x.Unit)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Select(x => x.Channel)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList()))
            .ToList();

    /// <summary>上电：每台 973 闭合本台全部工位电源开关，并同时闭合总电源（默认 319/320）。</summary>
    private static async Task PowerOnAsync(
        TaskContext ctx, TempPoint? tp, List<(int Unit, List<string> Channels)> map, CancellationToken ct)
    {
        foreach (var (unit, channels) in map)
        {
            var dmm = RequireDmm(ctx, unit);
            var masters = DmmAutoSlotMap.MasterPowerChannels();
            var closed = new List<string>();

            // 中途失败要把已经闭合的通道断开，绝不能把「半上电」状态留在设备上。
            try
            {
                foreach (var channel in channels)
                {
                    await dmm.CloseRelayAsync(channel, ct);
                    closed.Add(channel);
                }
                foreach (var master in masters)
                {
                    await dmm.CloseRelayAsync(master, ct);
                    closed.Add(master);
                }
            }
            catch
            {
                foreach (var channel in closed)
                {
                    try { await dmm.OpenRelayAsync(channel, CancellationToken.None); }
                    catch { /* 补救失败不再掩盖原始异常 */ }
                }
                throw;
            }

            AppLog.Info("Dmm",
                $"{Label(tp)} 973-{unit + 1} 上电：工位电源 {string.Join("/", channels)} + 总电源 {string.Join("/", masters)}");
        }

        var delay = GetSwitchDelayMs(ctx);
        if (delay > 0) await Task.Delay(delay, ct);
    }

    /// <summary>断电：每台 973 断开本台工位电源开关与总电源（默认 301-320）。</summary>
    private static async Task PowerOffAsync(
        TaskContext ctx, TempPoint? tp, List<(int Unit, List<string> Channels)> map, CancellationToken ct)
    {
        foreach (var (unit, channels) in map)
        {
            var dmm = RequireDmm(ctx, unit);
            var masters = DmmAutoSlotMap.MasterPowerChannels();

            // 先断工位电源再断总电源：与上电顺序相反，避免总电源带载切换。
            foreach (var channel in channels)
                await dmm.OpenRelayAsync(channel, ct);
            foreach (var master in masters)
                await dmm.OpenRelayAsync(master, ct);

            AppLog.Info("Dmm",
                $"{Label(tp)} 973-{unit + 1} 断电：{string.Join("/", channels.Concat(masters))} 全部断开");
        }

        var delay = GetSwitchDelayMs(ctx);
        if (delay > 0) await Task.Delay(delay, ct);
    }

    /// <summary>
    /// 取该台 973。取不到就抛，**不静默跳过** —— 工位表里既然有这台工位，
    /// 就必须能控到它；悄悄跳过等于让这批工位采不到数却显示正常。
    /// </summary>
    private static IDmm RequireDmm(TaskContext ctx, int unit) =>
        ctx.DmmAt(unit)
        ?? throw new InvalidOperationException(
            $"973-{unit + 1} 未连接，但工位表里包含该台的工位。请到「配置 → 设备」确认台数与 GPIB 地址。");

    private static string Label(TempPoint? tp) => tp is null ? "流程结束" : tp.Name;

    // ─── 采集 ───────────────────────────────────────────────────────────────

    /// <summary>遍历压力点采电压：正向写 USG，反向（回差）写 USG_R。</summary>
    private static async Task SweepPressureAsync(TaskContext ctx, TempPoint tp, bool reverse, CancellationToken ct)
    {
        var points = reverse
            ? ctx.Plan.PressurePoints.AsEnumerable().Reverse().Skip(1)
            : ctx.Plan.PressurePoints.AsEnumerable();

        foreach (var pp in points)
        {
            await ctx.WaitIfPausedAsync(ct);
            ct.ThrowIfCancellationRequested();

            ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";
            var column = $"{tp.Name}{pp.Name}_{(reverse ? "USG_R" : "USG")}";
            AppLog.Info("Run",
                $"万用表自动测试：压力点 {tp.Name}-{pp.Name} = {pp.Value}{ctx.Plan.PressureUnit} [{pp.PressureTypeDisplay}] → {column}");

            await RunPerformanceTestAction.SetAndWaitPressureAsync(ctx, pp, ct);
            await RunPerformanceTestAction.PressureHoldAsync(ctx, pp, ct);

            if (!ctx.Columns.Contains(column)) ctx.Columns.Add(column);
            var delayMs = GetSlotDelayMs(ctx);

            foreach (var slot in ctx.Slots.Entries)
            {
                ct.ThrowIfCancellationRequested();
                await ctx.WaitIfPausedAsync(ct);
                var unit = DmmAutoSlotMap.UnitIndexForSlot(SlotDacAddress.ParseSlotIndex(slot.Slot));
                var channel = VoltageChannel(slot);
                try
                {
                    var dmm = ctx.DmmAt(unit) ?? throw new InvalidOperationException($"973-{unit + 1} 未连接");
                    var value = await dmm.ReadVoltageAsync(channel, ct) * 1000.0;   // V → mV
                    ctx.Matrix.Set(slot.Slot, column, value, double.IsNaN(value) ? CellStatus.Error : CellStatus.Ok);
                    AppLog.Info("Read", $"{slot.Slot} {column}={value:G6} (973-{unit + 1} V{channel})");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Read", $"万用表电压 {slot.Slot} 通道{channel} 失败: {ex.Message}");
                    ctx.Matrix.Set(slot.Slot, column, double.NaN, CellStatus.Error);
                }
                if (delayMs > 0) await Task.Delay(delayMs, ct);
            }
        }
    }

    /// <summary>断电状态下逐工位读电阻，写 {Tn}_DMM_R（指标计算里优先用于 TCR）。</summary>
    private static async Task ReadResistanceAsync(TaskContext ctx, TempPoint tp, CancellationToken ct)
    {
        var column = $"{tp.Name}_DMM_R";
        if (!ctx.Columns.Contains(column)) ctx.Columns.Add(column);
        AppLog.Info("Run", $"万用表自动测试：{tp.Name} 断电状态下采集电阻 → {column}");

        var delayMs = GetSlotDelayMs(ctx);
        foreach (var slot in ctx.Slots.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            var unit = DmmAutoSlotMap.UnitIndexForSlot(SlotDacAddress.ParseSlotIndex(slot.Slot));
            var channel = ResistanceChannel(slot);
            try
            {
                var dmm = ctx.DmmAt(unit) ?? throw new InvalidOperationException($"973-{unit + 1} 未连接");
                var value = await dmm.ReadResistanceAsync(channel, ct);
                ctx.Matrix.Set(slot.Slot, column, value, double.IsNaN(value) ? CellStatus.Error : CellStatus.Ok);
                AppLog.Info("Read", $"{slot.Slot} {column}={value:G6} (973-{unit + 1} R{channel})");
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"万用表电阻 {slot.Slot} 通道{channel} 失败: {ex.Message}");
                ctx.Matrix.Set(slot.Slot, column, double.NaN, CellStatus.Error);
            }
            if (delayMs > 0) await Task.Delay(delayMs, ct);
        }
    }
}

public sealed class RunLongTermStabilityTestAction : IAction
{
    public string Key => "Run:LongTermStabilityTest";

    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct) =>
        LongTermStabilityRunner.RunAsync(ctx, ct);
}

// ─── Save:* / Cal:* ─────────────────────────────────────────────────────────
public sealed class SaveTestDataAction : IAction
{
    public string Key => "Save:TestData";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var metricCount = MetricsCalculator.Calculate(ctx);
        AppLog.Info("Cal", $"Metrics calculation complete for {metricCount} slots");
        RunPerformanceTestAction.SaveMatrix(ctx);
        return Task.CompletedTask;
    }
}
public sealed class CalTestAction : IAction
{
    public string Key => "Cal:Test";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        var count = MetricsCalculator.Calculate(ctx);
        AppLog.Info("Cal", $"指标重新计算完成，共 {count} 个工位");
        return Task.CompletedTask;
    }
}
