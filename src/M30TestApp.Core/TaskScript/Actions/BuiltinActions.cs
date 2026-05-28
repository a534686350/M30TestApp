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
                ctx.Matrix.Set(slot.Slot, col, value, CellStatus.Ok);
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"{measure} {slot.Slot} failed: {ex.Message}");
                ctx.Matrix.Set(slot.Slot, col, double.NaN, CellStatus.Error);
            }
        }
        AppLog.Info("Read", $"{measure} @ {col} for {ctx.Slots.Entries.Count} slots");
    }
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
        ReadHelper.ReadAndStoreAsync(ctx, "DMM",
            null!,
            async (dmm, slot, c) => await dmm.ReadVoltageAsync(slot.Channel, c), ct);
}

public sealed class RunPerformanceTestAction : IAction
{
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

        // ── 2. 逐温度点 ─────────────────────────────────────────
        for (var ti = startTi; ti < ctx.Plan.TempPoints.Count; ti++)
        {
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
                var pp = ctx.Plan.PressurePoints[pi];
                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";
                AppLog.Info("Run", $"开始测量当前温度点第{pi + 1}压力点：{tp.Name}:{tp.Celsius}; {pp.Name}:{pp.Value} [{pp.PressureTypeDisplay}]; V5:5; F:USG");

                if (ctx.Pressure is not null)
                    await SetAndWaitPressureAsync(ctx, pp, ct);

                var holdMs = GetDelayMs(ctx, "PressureAfterMs", 60000);
                AppLog.Info("Run", $"{pp.Value}{ctx.Plan.PressureUnit} 压力稳定中");
                await Task.Delay(Math.Max(0, holdMs), ct);

                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";

                if (!ctx.SkipUsg)
                {
                    AppLog.Info("Run", $"开始采集 {tp.Name}-{pp.Name} USG（全部工位，手动批量逻辑）");
                    await DacBatchSampler.SampleAllAsync(ctx, DacMeasureKind.Usig, $"{tp.Name}{pp.Name}_USG", pp.Value, tp.Celsius, ct);
                    AppLog.Info("Run", $"采集完成 {tp.Name}-{pp.Name} USG");
                }
                else
                {
                    AppLog.Info("Run", $"跳过 {tp.Name}-{pp.Name} USG 采集（用户选择）");
                }

                TestCheckpoint.Save(ctx, ti, pi + 1, leakDone);
            }

            await ReadOvenTempForAllSlots(ctx, tp, ct);
            TestCheckpoint.Save(ctx, ti + 1, 0, leakDone);
        }

        AppLog.Info("Run", "完成采集");
        SaveMatrix(ctx);
        TestCheckpoint.Delete();
        ctx.ResumeCheckpoint = null;

        if (ctx.Pressure is not null)
        {
            await ctx.Pressure.VentAsync(ct);
            AppLog.Info("Run", "泄压完成");
        }
        if (ctx.Oven is not null)
        {
            await ctx.Oven.StopAsync(ct);
            AppLog.Info("Run", "烘箱已停止");
        }
    }

    // ── 初始化设备：测试本轮用到的设备是否可连接 ───────────────────────────
    private static async Task InitDevicesAsync(TaskContext ctx, CancellationToken ct)
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

        // 根据实际使用的工位确定需要探漏的阀门编号
        var usedValveNos = ctx.Slots.Entries
            .Select(s => s.Valve)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToHashSet();

        // 从配置读取阀门地址，只保留实际用到的阀门
        var masterValve = ctx.Settings.Get("ValveSettings", "MasterValve", "");
        var valves = new List<(string name, string addr)>();
        for (var i = 1; i <= 8; i++)
        {
            if (!usedValveNos.Contains(i.ToString())) continue;
            var addr = ctx.Settings.Get("ValveSettings", $"Valve{i}", "");
            if (!string.IsNullOrWhiteSpace(addr)) valves.Add(($"阀门{i}", addr));
        }
        if (valves.Count == 0)
        {
            AppLog.Warn("Leak", "未找到需要探漏的阀门，跳过");
            return;
        }
        AppLog.Info("Leak", $"本次探漏阀门：{string.Join(", ", valves.Select(v => v.name))}（共{valves.Count}个）");

        var leakP = 10f; // 探漏压力 (kPa)，可配置
        var leakPrecision = 0.0005f;
        var leakCheckSec = 60; // 泄漏率判定等待秒数
        var switchMs = GetDelayMs(ctx, "ValveSwitchMs", 500);

        // 打开所有阀门
        AppLog.Info("Leak", "打开所有阀门");
        if (!string.IsNullOrWhiteSpace(masterValve))
            await ctx.Dmm.CloseRelayAsync(masterValve, ct);
        foreach (var (_, addr) in valves)
            await ctx.Dmm.CloseRelayAsync(addr, ct);
        await Task.Delay(switchMs, ct);

        // 泄压
        AppLog.Info("Leak", "泄压");
        await ctx.Pressure.VentAsync(ct);
        await Task.Delay(GetDelayMs(ctx, "VentWaitMs", 120000) / 10, ct); // 快速泄压等待

        // 逐阀探漏
        foreach (var (vName, vAddr) in valves)
        {
            ct.ThrowIfCancellationRequested();
            AppLog.Info("Leak", $"正在对{vName}进行探漏");
            try
            {
                // 关闭所有阀门，只开当前阀
                foreach (var (_, a) in valves) await ctx.Dmm.OpenRelayAsync(a, ct);
                await ctx.Dmm.CloseRelayAsync(vAddr, ct);
                await Task.Delay(switchMs, ct);

                // 加满量程压力
                AppLog.Info("Leak", $"0kPa满量程探漏开始");
                await ctx.Pressure.SetPressureAsync(leakP, "kPa", leakPrecision, ct);
                await Task.Delay(15000, ct); // 等稳定15秒

                // 读取并检查泄漏率
                AppLog.Info("Leak", "满量程探漏检查泄露率");
                var p1 = await ctx.Pressure.ReadPressureAsync(ct);
                await Task.Delay(leakCheckSec * 1000, ct);
                ct.ThrowIfCancellationRequested();
                var p2 = await ctx.Pressure.ReadPressureAsync(ct);
                var leakRate = Math.Abs(p2 - p1) / leakCheckSec;

                if (leakRate < leakPrecision)
                    AppLog.Info("Leak", $"{vName}探漏通过");
                else
                    AppLog.Warn("Leak", $"{vName}探漏失败，泄漏率={leakRate:G4}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Leak", $"{vName}探漏失败: {ex.Message}");
            }
        }

        // 全开探漏（仅在使用多个阀门时执行）
        if (valves.Count > 1)
        {
            AppLog.Info("Leak", "正在打开所有阀进行整体探漏");
            foreach (var (_, addr) in valves) await ctx.Dmm.CloseRelayAsync(addr, ct);
            await Task.Delay(switchMs, ct);
            try
            {
                AppLog.Info("Leak", "0kPa满量程探漏开始");
                await ctx.Pressure.SetPressureAsync(leakP, "kPa", leakPrecision, ct);
                await Task.Delay(10000, ct);
                AppLog.Info("Leak", "满量程探漏检查泄露率");
                var p1 = await ctx.Pressure.ReadPressureAsync(ct);
                await Task.Delay(leakCheckSec * 1000, ct);
                ct.ThrowIfCancellationRequested();
                var p2 = await ctx.Pressure.ReadPressureAsync(ct);
                var rate = Math.Abs(p2 - p1) / leakCheckSec;
                if (rate < leakPrecision)
                    AppLog.Info("Leak", "阀门整体探漏通过");
                else
                    AppLog.Warn("Leak", $"阀门整体探漏失败，泄漏率={rate:G4}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Leak", $"整体探漏失败: {ex.Message}");
            }
        }

        // 泄压结束
        await ctx.Pressure.VentAsync(ct);
        AppLog.Info("Leak", "探漏已完成！泄压");
    }

    // ── 设置烘箱并等待温度到达（1分钟间隔日志，最长240分钟）───────────────
    internal static async Task SetAndWaitOvenAsync(TaskContext ctx, TempPoint tp, string source, CancellationToken ct)
    {
        try
        {
            AppLog.Info(source, $"设置烘箱温度至{tp.Celsius}度");
            await ctx.Oven!.SetTempAsync(tp.Celsius, ct);
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
            var remaining = totalMinutes - min;
            ctx.CurrentTemp = $"{tpName} 保温中 {min}/{totalMinutes} min (剩余{remaining}min)";
            AppLog.Info("Run", $"烘箱已保温 {min} min, 还剩 {remaining} min");
            if (min < totalMinutes)
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    // ── 设置压力并等待稳定 ───────────────────────────────────────────────
    private static async Task SetAndWaitPressureAsync(TaskContext ctx, PressurePoint pp, CancellationToken ct)
    {
        // 自动切换压力类型（绝压/表压/差压）
        await ctx.Pressure!.SetPressureTypeAsync(pp.PressureType, ct);
        AppLog.Info("Run", $"压力类型切换为 {pp.PressureType} ({pp.PressureTypeDisplay})");

        await ctx.Pressure.SetPressureAsync(pp.Value, ctx.Plan.PressureUnit, ctx.Plan.Precision, ct);
        for (var i = 0; i < 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            var current = await ctx.Pressure.ReadPressureAsync(ct);
            ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit} [{pp.PressureTypeDisplay}]";
            if (Math.Abs(current - pp.Value) <= ctx.Plan.Precision) break;
            await Task.Delay(500, ct);
        }
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
    }

    private static string SafePath(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "未命名方案" : safe;
    }
}

// ─── Save:* / Cal:* ─────────────────────────────────────────────────────────
public sealed class SaveTestDataAction : IAction
{
    public string Key => "Save:TestData";
    public Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct)
    {
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
