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
    public async Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
        if (ctx.Power is not null)
        {
            try
            {
                await ctx.Power.OutputOffAsync(ct);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DAQ", $"Power down skipped: {ex.Message}");
            }
        }
        AppLog.Info("DAQ", "Power down");
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
            for (int i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                var v = await ctx.Pressure.ReadPressureAsync(ct);
                if (Math.Abs(v - pp.Value) <= ctx.Plan.Precision) break;
                await Task.Delay(50, ct);
            }
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

        foreach (var slot in ctx.Slots.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                double value = 0;
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
    private sealed record ValveGroup(string ValveNo, string Address, List<SlotEntry> Slots);

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
                await ctx.Pressure.OpenAsync(ct);
                await ctx.Pressure.SetMeasureAsync(ct);
                AppLog.Info("Init", $"压力控制器 {ctx.Pressure.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"压力控制器连接失败: {ex.Message}"); }
        }

        if (ctx.Oven is not null)
        {
            try
            {
                await ctx.Oven.OpenAsync(ct);
                AppLog.Info("Init", $"烘箱 {ctx.Oven.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"烘箱连接失败: {ex.Message}"); }
        }

        if (ctx.Dmm is not null)
        {
            try
            {
                await ctx.Dmm.OpenAsync(ct);
                AppLog.Info("Init", $"DMM {ctx.Dmm.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"DMM连接失败: {ex.Message}"); }
        }

        if (ctx.Dac is not null)
        {
            try
            {
                await ctx.Dac.OpenAsync(ct);
                AppLog.Info("Init", $"采集板 {ctx.Dac.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"采集板连接失败: {ex.Message}"); }
        }

        if (ctx.Power is not null)
        {
            try
            {
                await ctx.Power.OpenAsync(ct);
                if (ctx.Power.State == ConnectionState.Connected)
                    AppLog.Info("Init", $"电源 {ctx.Power.Model} 已连接");
                else
                    AppLog.Warn("Init", $"电源 {ctx.Power.Model} 未连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"电源连接失败: {ex.Message}"); }
        }

        if (ctx.Board is not null)
        {
            try
            {
                // Board 和 Dac 可能共享串口
                var sharesPort = ctx.Dac is not null &&
                    string.Equals(ctx.Board.Address, ctx.Dac.Address, StringComparison.OrdinalIgnoreCase);
                if (!sharesPort)
                    await ctx.Board.OpenAsync(ct);
                AppLog.Info("Init", $"板卡 {ctx.Board.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"板卡连接失败: {ex.Message}"); }
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
                g.Key,
                ctx.Settings.Get("ValveSettings", $"Valve{g.Key}", ""),
                g.OrderBy(s => SlotDacAddress.ParseSlotIndex(s.Slot)).ToList()))
            .Where(g => !string.IsNullOrWhiteSpace(g.Address))
            .ToList();
    }

    private static string ResolveValveNo(SlotEntry slot)
    {
        var slotIndex = SlotDacAddress.ParseSlotIndex(slot.Slot);
        if (slotIndex <= 0) return "";
        return ((slotIndex - 1) / 32 + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task PreparePressureValveRoutingAsync(TaskContext ctx, CancellationToken ct)
    {
        if (ctx.Dmm is null) return;

        var groups = GetValveGroups(ctx);
        if (groups.Count == 0) return;

        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);
        var ventWaitMs = Math.Max(switchMs, GetDelayMs(ctx, "VentWaitMs", 120000) / 10);

        AppLog.Info("Valve", "开始加压测试前先打开全部阀门排空气路");
        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.OpenRelayAsync(masterValve, ct);
        foreach (var group in groups)
            await ctx.Dmm.OpenRelayAsync(group.Address, ct);
        await Task.Delay(switchMs, ct);

        if (ctx.Pressure is not null)
        {
            await ctx.Pressure.VentAsync(ct);
            await Task.Delay(ventWaitMs, ct);
        }

        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.CloseRelayAsync(masterValve, ct);
        AppLog.Info("Valve", $"排空完成，已关闭总阀 {masterValve}");
    }

    private static async Task ActivateValveGroupAsync(TaskContext ctx, ValveGroup targetGroup, string source, CancellationToken ct)
    {
        if (ctx.Dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.CloseRelayAsync(masterValve, ct);
        foreach (var group in groups)
            await ctx.Dmm.CloseRelayAsync(group.Address, ct);

        await ctx.Dmm.OpenRelayAsync(targetGroup.Address, ct);
        await Task.Delay(switchMs, ct);
        AppLog.Info(source, $"阀门切换：关闭总阀 {masterValve}，只打开 {targetGroup.ValveNo} 号阀 {targetGroup.Address}");
    }

    private static async Task OpenAllPressureValvesAsync(TaskContext ctx, string source, CancellationToken ct)
    {
        if (ctx.Dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.OpenRelayAsync(masterValve, ct);
        foreach (var group in groups)
            await ctx.Dmm.OpenRelayAsync(group.Address, ct);
        await Task.Delay(switchMs, ct);
        AppLog.Info(source, "已打开全部阀门用于泄压/排空");
    }

    private static async Task CloseAllPressureValvesAsync(TaskContext ctx, string source, CancellationToken ct)
    {
        if (ctx.Dmm is null) return;

        var groups = GetValveGroups(ctx);
        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.CloseRelayAsync(masterValve, ct);
        foreach (var group in groups)
            await ctx.Dmm.CloseRelayAsync(group.Address, ct);
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
            await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Usig, column, pp.Value, tp.Celsius, ct);
            return;
        }

        foreach (var group in groups)
        {
            await ctx.WaitIfPausedAsync(ct);
            await ActivateValveGroupAsync(ctx, group, "Run", ct);

            if (ctx.Pressure is not null)
                await SetAndWaitPressureAsync(ctx, pp, ct);
            await PressureHoldAsync(ctx, pp, ct);

            ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit} / 阀{group.ValveNo}";
            await DacBatchSampler.SampleAllAsync(
                ctx,
                DacMeasureKind.Usig,
                column,
                pp.Value,
                tp.Celsius,
                ct,
                slotsOverride: group.Slots);
        }
    }

    private static async Task PerformLeakCheckAsync(TaskContext ctx, CancellationToken ct)
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

        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        AppLog.Info("Leak", $"本次探漏阀组：{string.Join(", ", valveGroups.Select(v => $"阀{v.ValveNo}({v.Slots.Count}工位)"))}（共{valveGroups.Count}组）");

        var leakPressures = LeakCheckPlanHelper.ResolvePressures(ctx.Plan);
        if (leakPressures.Count == 0)
        {
            AppLog.Warn("Leak", "未配置探漏压力点且无法从方案推导，跳过探漏");
            return;
        }

        var leakPrecision = LeakCheckPlanHelper.ResolvePrecision(ctx.Plan);
        var pressureUnit = string.IsNullOrWhiteSpace(ctx.Plan.PressureUnit) ? "kPa" : ctx.Plan.PressureUnit;
        var leakCheckSecMs = GetDelayMs(ctx, "LeakCheckSec", 60000);
        var leakCheckSec = Math.Max(1, leakCheckSecMs >= 1000 ? leakCheckSecMs / 1000 : leakCheckSecMs);
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);
        var pressureType = ctx.Plan.DefaultPressureType;

        AppLog.Info("Leak",
            $"探漏参数：类型={pressureType}，压力点=[{string.Join(", ", leakPressures)}]{pressureUnit}，精度/泄漏率阈值={leakPrecision}{pressureUnit}/s，观测={leakCheckSec}s");

        await ctx.Pressure.SetPressureTypeAsync(pressureType, ct);

        // 初始状态：全部阀门关闭（主阀始终保持关闭）
        AppLog.Info("Leak", "关闭所有阀门");
        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.CloseRelayAsync(masterValve, ct);
        foreach (var group in valveGroups)
            await ctx.Dmm.CloseRelayAsync(group.Address, ct);
        await Task.Delay(switchMs, ct);

        // 泄压
        AppLog.Info("Leak", "泄压");
        await ctx.Pressure.VentAsync(ct);
        await Task.Delay(GetDelayMs(ctx, "VentWaitMs", 120000) / 10, ct);

        foreach (var leakP in leakPressures)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Info("Leak", $"── 探漏压力点 {leakP}{pressureUnit} ──");

            // 逐阀探漏：每次只开被测阀，其余全关，主阀不动
            foreach (var group in valveGroups)
            {
                ct.ThrowIfCancellationRequested();
                var vName = $"阀{group.ValveNo}";
                var vAddr = group.Address;
                AppLog.Info("Leak", $"正在对{vName}进行探漏 @ {leakP}{pressureUnit}");
                try
                {
                    foreach (var g in valveGroups) await ctx.Dmm.CloseRelayAsync(g.Address, ct);
                    await ctx.Dmm.OpenRelayAsync(vAddr, ct);
                    await Task.Delay(switchMs, ct);

                    if (!await RunLeakRateCheckAsync(ctx, $"{vName} @ {leakP}{pressureUnit}", leakP, pressureUnit, leakPrecision, leakCheckSec, 15, ct))
                        AppLog.Warn("Leak", $"{vName} @ {leakP}{pressureUnit} 探漏失败");

                    await ctx.Dmm.CloseRelayAsync(vAddr, ct);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Leak", $"{vName} @ {leakP}{pressureUnit} 探漏失败: {ex.Message}");
                }
            }

        }

        // 泄压结束
        await ctx.Pressure.VentAsync(ct);
        AppLog.Info("Leak", "探漏完成，已泄压，所有阀门已关闭");
    }

    private static async Task<bool> RunLeakRateCheckAsync(
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

        AppLog.Info("Leak", $"{label} 检查泄漏率（观测 {observeSec}s）");
        var p1 = await ctx.Pressure.ReadPressureAsync(ct);
        await Task.Delay(observeSec * 1000, ct);
        ct.ThrowIfCancellationRequested();
        var p2 = await ctx.Pressure.ReadPressureAsync(ct);
        var leakRate = Math.Abs(p2 - p1) / observeSec;

        if (leakRate < precision)
        {
            AppLog.Info("Leak", $"{label} 探漏通过，泄漏率={leakRate:G4}{unit}/s");
            return true;
        }

        AppLog.Warn("Leak", $"{label} 探漏失败，泄漏率={leakRate:G4}{unit}/s（阈值<{precision}{unit}/s）");
        return false;
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
                AppLog.Warn(source, $"设置烘箱温度或启动加热失败（TEMP/POWER 指令未返回 OK）");
                return;
            }
            AppLog.Info(source, "烘箱已发送加热指令（TEMP + POWER,ON）");
        }
        catch (Exception ex)
        {
            AppLog.Warn(source, $"设置烘箱温度失败，跳过等待：{ex.Message}");
            return;
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
                        AppLog.Warn(source, "连续3次读取温度NaN，烘箱通讯可能异常，跳过加热等待直接进入保温");
                        return;
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
                    AppLog.Warn(source, "连续3次读取失败，跳过加热等待");
                    return;
                }
            }
            if (min < maxMinutes)
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
        AppLog.Warn(source, $"加热超时({maxMinutes}min)，继续进入保温");
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

    // ── 设置压力并等待稳定 ───────────────────────────────────────────────
    internal static async Task SetAndWaitPressureAsync(TaskContext ctx, PressurePoint pp, CancellationToken ct)
    {
        if (ctx.Pressure is null)
        {
            AppLog.Info("Run", $"跳过压力设置：压力控制器未启用（{pp.Name}={pp.Value}{ctx.Plan.PressureUnit}）");
            return;
        }

        // 自动切换压力类型（绝压/表压/差压）
        await ctx.Pressure.SetPressureTypeAsync(pp.PressureType, ct);
        AppLog.Info("Run", $"压力类型切换为 {pp.PressureType} ({pp.PressureTypeDisplay})");

        await ctx.Pressure.SetPressureAsync(pp.Value, ctx.Plan.PressureUnit, ctx.Plan.Precision, ct);
        for (var i = 0; i < 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            var current = await ctx.Pressure.ReadPressureAsync(ct);
            var diff = Math.Abs(current - pp.Value);
            ctx.CurrentPressure = $"{pp.Name} 目标{pp.Value} 当前{current:F4} 差值{diff:F4} {ctx.Plan.PressureUnit}";
            if (i % 10 == 0)
                AppLog.Info("Run", $"等待压力稳定：目标={pp.Value} 当前={current:F4} 差值={diff:F4} ({i}/120)");
            if (diff <= ctx.Plan.Precision)
            {
                AppLog.Info("Run", $"压力已稳定：{current:F4}{ctx.Plan.PressureUnit}（目标{pp.Value}，精度{ctx.Plan.Precision}）");
                break;
            }
            await Task.Delay(500, ct);
        }
    }

    /// <summary>压力保持等待，带倒计时日志。</summary>
    internal static async Task PressureHoldAsync(TaskContext ctx, PressurePoint pp, CancellationToken ct)
    {
        if (ctx.Pressure is null)
        {
            AppLog.Info("Run", $"跳过保压：压力控制器未启用（{pp.Name}={pp.Value}{ctx.Plan.PressureUnit}）");
            return;
        }

        var holdMs = GetDelayMs(ctx, "PressureAfterMs", 60000);
        if (holdMs <= 0) return;
        var holdSec = holdMs / 1000;
        AppLog.Info("Run", $"{pp.Value}{ctx.Plan.PressureUnit} 开始保压 {holdSec}s");
        for (var s = 0; s < holdSec; s++)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            ctx.CurrentPressure = $"{pp.Name} {pp.Value}{ctx.Plan.PressureUnit} 保压中 {s}/{holdSec}s";
            await Task.Delay(1000, ct);
        }
        AppLog.Info("Run", $"{pp.Value}{ctx.Plan.PressureUnit} 保压完成");
    }

    // ── 读取烘箱实际温度，写入所有工位（每温度点采集完成后调用一次）─────────
    private static async Task ReadOvenTempForAllSlots(TaskContext ctx, TempPoint tp, CancellationToken ct)
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
        int.TryParse(ctx.Settings.Get("DelaySettings", key, fallback.ToString(CultureInfo.InvariantCulture)), out var ms) ? ms : fallback;

    public static void SaveMatrix(TaskContext ctx)
    {
        if (UseNewSaveLayout())
        {
            SaveMatrixWithCurrentLayout(ctx);
            return;
        }

        var planDir = Path.Combine(AppPaths.DataDir, SafePath(ctx.Plan.Name));
        Directory.CreateDirectory(planDir);
        var sensor = string.IsNullOrWhiteSpace(ctx.Plan.SensorType) ? "传感器" : ctx.Plan.SensorType;
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyyMMdd");
        var timeStr = now.ToString("HHmmss");

        // auto-increment sequence number for today
        var existing = Directory.GetFiles(planDir, $"{dateStr}*.*");
        var seq = existing.Length + 1;

        // format: 20260521 142145-02 HRT-HP-K250-G20(性能测试).csv
        var fileName = $"{dateStr} {timeStr}-{seq:D2} {SafePath(sensor)}(性能测试).csv";
        var file = Path.Combine(planDir, fileName);
        var serialMap = ctx.Slots.Entries.ToDictionary(s => s.Slot, s => s.SerialNo);
        ctx.Matrix.ExportCsv(file, ctx.Columns, serialMap);
        AppLog.Info("Save", $"数据保存到 {file}");

        // M30方案额外导出旧版格式 CSV 到桌面
        if (LegacyCsvExporter.IsLegacyProfile(ctx.Plan))
        {
            try
            {
                LegacyCsvExporter.Export(ctx);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Save", $"旧版格式CSV导出失败: {ex.Message}");
            }
        }
    }

    private static bool UseNewSaveLayout() => true;

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
        if (LegacyCsvExporter.IsLegacyProfile(ctx.Plan))
        {
            var csvName = $"{dateStr} {timeStr}-{seq:D2} {safeSensor}(性能测试).csv";
            var csv = Path.Combine(batchDir, csvName);
            ctx.Matrix.ExportCsv(csv, ctx.Columns, serialMap);
            AppLog.Info("Save", $"数据保存到 {csv}");

            try
            {
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
    }

    private static string SafePath(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "未命名方案" : safe;
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
