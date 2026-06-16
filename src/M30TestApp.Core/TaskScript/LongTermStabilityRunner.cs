using System.Globalization;
using System.IO;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;
using M30TestApp.Core.TaskScript.Actions;

namespace M30TestApp.Core.TaskScript;

/// <summary>
/// Long-term stability test orchestration: oven soak, pressure points, DAQ973A voltage/resistance + oven temp.
/// </summary>
public static class LongTermStabilityRunner
{
    public static async Task RunAsync(TaskContext ctx, CancellationToken ct)
    {
        PrepareMatrix(ctx);

        var measureLabel = ctx.LongTermMeasureMode == LongTermStabilityMeasureMode.Resistance ? "电阻" : "电压";
        var slotNos = ctx.Slots.Entries
            .Select(s => int.TryParse(s.Slot.Replace("Slot", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : 0)
            .Where(n => n > 0)
            .OrderBy(n => n)
            .ToList();
        var slotRange = slotNos.Count > 0
            ? LongTermStabilitySlotMap.FormatRangeHint(slotNos[0], slotNos[^1])
            : "无工位";
        AppLog.Info("Run", $"开始长期稳定性测试：方案={ctx.Plan.Name}，传感器={ctx.Plan.SensorType}，{slotRange}，采集={measureLabel}");
        await InitDevicesAsync(ctx, ct);

        for (var ti = 0; ti < ctx.Plan.TempPoints.Count; ti++)
        {
            await ctx.WaitIfPausedAsync(ct);
            var tp = ctx.Plan.TempPoints[ti];
            ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";
            ctx.CurrentPressure = "";
            AppLog.Info("Run", $"长期稳定性：温度点 {tp.Name} ({ti + 1}/{ctx.Plan.TempPoints.Count})");

            if (ctx.Oven is null)
            {
                AppLog.Warn("Run", "长期稳定性：烘箱未配置，跳过温度设置和保温");
            }
            else if (!await EnsureOvenConnectedAsync(ctx, ct))
            {
                AppLog.Warn("Run", "长期稳定性：烘箱连接失败，跳过温度设置和保温");
            }
            else
            {
                await RunPerformanceTestAction.SetAndWaitOvenAsync(ctx, tp, "Run", ct);
                var soakMinutes = tp.SoakMinutes ?? (int.TryParse(ctx.Settings.Get("DelaySettings", "SoakMinutes", "120"), out var sm) ? sm : 120);
                AppLog.Info("Run", $"长期稳定性：开始保温 {soakMinutes} min");
                await RunPerformanceTestAction.SoakWithLogAsync(ctx, soakMinutes, tp.Name, ct);
                AppLog.Info("Run", "长期稳定性：保温完成");
            }

            foreach (var pp in ctx.Plan.PressurePoints)
            {
                await ctx.WaitIfPausedAsync(ct);
                ctx.CurrentTemp = $"{tp.Name}: {tp.Celsius}℃";
                ctx.CurrentPressure = $"{pp.Name}: {pp.Value} {ctx.Plan.PressureUnit}";
                AppLog.Info("Run", $"长期稳定性：压力点 {tp.Name}-{pp.Name} = {pp.Value}{ctx.Plan.PressureUnit} [{pp.PressureTypeDisplay}]");

                if (ctx.Pressure is not null)
                    await RunPerformanceTestAction.SetAndWaitPressureAsync(ctx, pp, ct);

                await RunPerformanceTestAction.PressureHoldAsync(ctx, pp, ct);
                await ReadDmmForAllSlotsAsync(ctx, tp, pp, ct);
            }

            await ReadOvenTempOnceAsync(ctx, tp, ct);
        }

        AppLog.Info("Run", $"长期稳定性：{measureLabel}采集完成");
        SaveData(ctx);

        if (ctx.Pressure is not null)
        {
            await ctx.Pressure.VentAsync(ct);
            AppLog.Info("Run", "长期稳定性：泄压完成");
        }
        if (ctx.Oven is not null)
        {
            await ctx.Oven.StopAsync(ct);
            AppLog.Info("Run", "长期稳定性：烘箱已停止");
        }
    }

    public static void PrepareMatrix(TaskContext ctx)
    {
        ctx.Matrix.Clear();
        ctx.Columns.Clear();
        foreach (var slot in ctx.Slots.Entries) ctx.Matrix.EnsureSlot(slot.Slot);
        foreach (var col in LongTermStabilityMatrix.BuildColumns(ctx.Plan, ctx.LongTermMeasureMode))
            ctx.Columns.Add(col.Key);
    }

    public static void SaveData(TaskContext ctx)
    {
        var dir = Path.Combine(AppPaths.DataDir, "\u957F\u671F\u7A33\u5B9A\u6027");
        Directory.CreateDirectory(dir);

        var sensor = string.IsNullOrWhiteSpace(ctx.Plan.SensorType) ? ctx.Plan.Name : ctx.Plan.SensorType;
        var safeSensor = SafePath(sensor);
        var fileBase = $"{safeSensor}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var xlsx = Path.Combine(dir, fileBase + ".xlsx");
        var csv = Path.Combine(dir, fileBase + ".csv");
        var serialMap = ctx.Slots.Entries.ToDictionary(s => s.Slot, s => s.SerialNo);
        var headerMap = LongTermStabilityMatrix.BuildFlatHeaderMap(ctx.Plan, ctx.LongTermMeasureMode);

        LongTermStabilityExporter.Export(ctx.Matrix, ctx.Plan, xlsx, serialMap, ctx.LongTermMeasureMode);
        ctx.Matrix.ExportCsv(csv, ctx.Columns, serialMap, headerMap);
        AppLog.Info("Save", $"Long-term stability data saved: {xlsx}");
    }

    private static async Task InitDevicesAsync(TaskContext ctx, CancellationToken ct)
    {
        AppLog.Info("Init", "长期稳定性：初始化压力控制器、烘箱和 DAQ973A/DMM…");

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
                var opened = await ctx.Oven.OpenAsync(ct);
                if (opened)
                    AppLog.Info("Init", $"烘箱 {ctx.Oven.Model} 已连接");
                else
                    AppLog.Warn("Init", $"烘箱 {ctx.Oven.Model} 连接失败（状态={ctx.Oven.State}）");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"烘箱连接失败: {ex.Message}"); }
        }

        if (ctx.Dmm is not null)
        {
            try
            {
                await ctx.Dmm.OpenAsync(ct);
                AppLog.Info("Init", $"DAQ973A/DMM {ctx.Dmm.Model} 已连接");
            }
            catch (Exception ex) { AppLog.Warn("Init", $"DAQ973A/DMM 连接失败: {ex.Message}"); }
        }

        AppLog.Info("Init", "长期稳定性：设备初始化完成");
    }

    private static async Task<bool> EnsureOvenConnectedAsync(TaskContext ctx, CancellationToken ct)
    {
        if (ctx.Oven is null) return false;
        if (ctx.Oven.State == ConnectionState.Connected) return true;

        AppLog.Info("Run", $"长期稳定性：烘箱当前状态 {ctx.Oven.State}，尝试重新连接…");
        try
        {
            return await ctx.Oven.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Run", $"长期稳定性：烘箱重新连接失败: {ex.Message}");
            return false;
        }
    }

    private static async Task ReadDmmForAllSlotsAsync(TaskContext ctx, TempPoint tp, PressurePoint pp, CancellationToken ct)
    {
        var colKey = LongTermStabilityMatrix.DmmKey(tp, pp, ctx.LongTermMeasureMode);
        var slotDelayMs = GetDmmSlotDelayMs(ctx);
        var isResistance = ctx.LongTermMeasureMode == LongTermStabilityMeasureMode.Resistance;
        var measureName = isResistance ? "电阻 Ω" : "电压 mV";
        var dmmCmd = isResistance ? "CONF:RES (@CH) → READ?" : "CONF:VOLT (@CH) → READ?";

        if (ctx.Dmm is null)
        {
            AppLog.Warn("Read", "DAQ973A/DMM 未连接，无法采集长期稳定性数据");
            foreach (var slot in ctx.Slots.Entries)
                ctx.Matrix.Set(slot.Slot, colKey, double.NaN, CellStatus.Error);
            return;
        }

        AppLog.Info("Read", $"长期稳定性：开始采集 {tp.Name}-{pp.Name} DAQ973A {measureName}（{dmmCmd}，全部工位，间隔 {slotDelayMs}ms）");
        foreach (var slot in ctx.Slots.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.WaitIfPausedAsync(ct);
            try
            {
                var raw = isResistance
                    ? await ctx.Dmm.ReadResistanceAsync(slot.Channel, ct)
                    : await ctx.Dmm.ReadVoltageAsync(slot.Channel, ct) * 1000.0;
                ctx.Matrix.Set(slot.Slot, colKey, raw, double.IsNaN(raw) ? CellStatus.Error : CellStatus.Ok);
                AppLog.Info("Read", $"长期稳定性：{tp.Name}-{pp.Name} {slot.Slot} SN={slot.SerialNo} CH={slot.Channel} = {raw:G6} {(isResistance ? "Ω" : "mV")}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"长期稳定性 {measureName} {slot.Slot} channel={slot.Channel} failed: {ex.Message}");
                ctx.Matrix.Set(slot.Slot, colKey, double.NaN, CellStatus.Error);
            }
            if (slotDelayMs > 0)
                await Task.Delay(slotDelayMs, ct);
        }
        AppLog.Info("Read", $"长期稳定性：采集完成 {tp.Name}-{pp.Name} DAQ973A {measureName}");
    }

    private static async Task ReadOvenTempOnceAsync(TaskContext ctx, TempPoint tp, CancellationToken ct)
    {
        var tempKey = LongTermStabilityMatrix.TempKey(tp);
        var firstSlot = ctx.Slots.Entries.FirstOrDefault()?.Slot;

        double ovenTemp = double.NaN;
        if (ctx.Oven is not null)
        {
            try
            {
                ovenTemp = await ctx.Oven.ReadTempAsync(ct);
                AppLog.Info("Read", $"长期稳定性：{tp.Name} 全部压力点采集完成，烘箱实际温度 {ovenTemp:F5}°C（仅读一次）");
            }
            catch (Exception ex)
            {
                AppLog.Error("Read", $"长期稳定性 {tp.Name} 读取烘箱温度失败：{ex.Message}");
            }
        }
        else
        {
            AppLog.Warn("Read", $"长期稳定性：{tp.Name} 烘箱未连接，跳过温度采集");
        }

        var status = double.IsNaN(ovenTemp) ? CellStatus.Error : CellStatus.Ok;
        if (firstSlot is not null)
            ctx.Matrix.Set(firstSlot, tempKey, ovenTemp, status);
    }

    private static int GetDmmSlotDelayMs(TaskContext ctx) =>
        int.TryParse(ctx.Settings.Get("DelaySettings", "DmmSlotDelayMs", "100"), out var ms)
            ? Math.Max(0, ms)
            : 100;

    private static string SafePath(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "plan" : safe;
    }
}
