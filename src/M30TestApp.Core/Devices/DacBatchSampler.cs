using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Devices;

/// <summary>
/// 与手动页「批量采集」相同的逐工位采集逻辑（按工位序号、板卡/通道寻址、UT 按板卡切继电器）。
/// </summary>
public enum DacMeasureKind
{
    UT,
    Usource,
    Isource,
    Usig,
}

public static class DacBatchSampler
{
    public static DacMeasureKind? ParseMeasure(string measure) => measure switch
    {
        "UT" or "Ut" => DacMeasureKind.UT,
        "Usource" or "USC" => DacMeasureKind.Usource,
        "Isource" or "ISC" => DacMeasureKind.Isource,
        "Usig" or "USG" => DacMeasureKind.Usig,
        _ => null,
    };

    /// <summary>
    /// 遍历 <see cref="TaskContext.Slots"/> 全部工位，写入矩阵列 <paramref name="matrixColumn"/>。
    /// </summary>
    public static async Task SampleAllAsync(
        TaskContext ctx,
        DacMeasureKind measure,
        string matrixColumn,
        float pressure,
        float tempC,
        CancellationToken ct = default,
        Action<SlotEntry, double, bool>? onSlotComplete = null,
        int? startSlot = null,
        int? endSlot = null,
        int customIntervalMs = -1)
    {
        if (ctx.Dac is null)
        {
            AppLog.Warn("Read", "采集板未配置，跳过批量采集");
            return;
        }

        await ctx.Dac.OpenAsync(ct).ConfigureAwait(false);
        if (!ctx.Columns.Contains(matrixColumn))
            ctx.Columns.Add(matrixColumn);

        // Get delay based on measure type
        var ioDelay = customIntervalMs >= 0 ? customIntervalMs : GetMeasureDelay(ctx.Settings, measure);
        var switchMs = GetDelayMs(ctx.Settings, "ValveSwitchMs", 500);
        var lastUtCard = -1;

        List<SlotEntry> slots;
        if (startSlot is >= 1 && endSlot is int end && end >= startSlot.Value)
        {
            slots = new List<SlotEntry>(endSlot.Value - startSlot.Value + 1);
            for (var n = startSlot.Value; n <= endSlot.Value; n++)
                slots.Add(ResolveSlot(ctx, n));
        }
        else
        {
            slots = ctx.Slots.Entries
                .OrderBy(s => SlotDacAddress.ParseSlotIndex(s.Slot))
                .ToList();
        }

        AppLog.Info("Read", $"批量采集 {measure}：{slots.Count} 工位 → 列 {matrixColumn} @ P={pressure:G4}, T={tempC:G2} 间隔={ioDelay}ms");

        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Matrix.EnsureSlot(slot.Slot);
            var (card, channel) = SlotDacAddress.Get(slot);

            // 只有测 UT 时才切 UT 电源（同板卡的16个工位共用一次切换）。
            // USC/ISC/USG 不需要切电源，直接采集。
            if (measure == DacMeasureKind.UT &&
                int.TryParse(card, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cardNum) &&
                cardNum != lastUtCard)
            {
                await SwitchUtPowerForCardAsync(ctx, cardNum, switchMs, ct).ConfigureAwait(false);
                lastUtCard = cardNum;
            }

            try
            {
                var value = await ReadAsync(ctx.Dac, measure, pressure, tempC, card, channel, ct).ConfigureAwait(false);
                ctx.Matrix.Set(slot.Slot, matrixColumn, value, CellStatus.Ok);
                AppLog.Info("Read", $"{slot.Slot} {matrixColumn}={value:G6} (卡{card} 通{channel})");
                onSlotComplete?.Invoke(slot, value, true);
            }
            catch (Exception ex)
            {
                ctx.Matrix.Set(slot.Slot, matrixColumn, double.NaN, CellStatus.Error);
                AppLog.Error("Read", $"{slot.Slot} {matrixColumn} 卡{card} 通{channel} 失败: {ex.Message}");
                onSlotComplete?.Invoke(slot, double.NaN, false);
            }

            if (ioDelay > 0)
                await Task.Delay(ioDelay, ct).ConfigureAwait(false);
        }

        // UT 采集完成后关闭所有板卡的 UT 电源（USC/ISC/USG 不需要电源）
        if (measure == DacMeasureKind.UT)
        {
            await TurnOffAllUtPowerAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    private static async Task TurnOffAllUtPowerAsync(TaskContext ctx, CancellationToken ct)
    {
        if (ctx.Dmm is null || ctx.Dmm.State != ConnectionState.Connected) return;
        // UT 电源通道（3xx）默认状态：ROUT:OPEN（与原始程序 CloseRelayCh 一致）
        for (var i = 1; i <= 16; i++)
        {
            var ch = ctx.Settings.Get("SwitchUnitCards", $"Card{i}", (300 + i).ToString());
            await ctx.Dmm.OpenRelayAsync(ch, ct).ConfigureAwait(false);
        }
        AppLog.Info("Read", "UT采集结束，关闭全部板卡UT电源");
    }

    private static int GetMeasureDelay(IniFile settings, DacMeasureKind measure) => measure switch
    {
        DacMeasureKind.Usig => GetDelayMs(settings, "UsigDelayMs", 300),
        DacMeasureKind.UT => GetDelayMs(settings, "UtDelayMs", 300),
        DacMeasureKind.Usource => GetDelayMs(settings, "UsourceDelayMs", 300),
        DacMeasureKind.Isource => GetDelayMs(settings, "IsourceDelayMs", 300),
        _ => GetDelayMs(settings, "StableIoMs", 300)
    };

    private static async Task SwitchUtPowerForCardAsync(TaskContext ctx, int cardAddr, int switchMs, CancellationToken ct)
    {
        if (ctx.Dmm is null) return;
        if (ctx.Dmm.State != ConnectionState.Connected)
            await ctx.Dmm.OpenAsync(ct).ConfigureAwait(false);

        var dmmChannel = ctx.Settings.Get("SwitchUnitCards", $"Card{cardAddr}", (300 + cardAddr).ToString());
        // 与原始程序 FormTest.cs:2866 一致：目标→OpenRelayCh→ROUT:CLOSE，其余→CloseRelayCh→ROUT:OPEN
        foreach (var i in Enumerable.Range(1, 16))
        {
            if (i == cardAddr) continue;
            var ch = ctx.Settings.Get("SwitchUnitCards", $"Card{i}", (300 + i).ToString());
            await ctx.Dmm.OpenRelayAsync(ch, ct).ConfigureAwait(false);
        }
        await ctx.Dmm.CloseRelayAsync(dmmChannel, ct).ConfigureAwait(false);
        await Task.Delay(switchMs, ct).ConfigureAwait(false);
        AppLog.Info("Read", $"切换UT电源：板卡{cardAddr} 通道{dmmChannel} 通电");
    }

    private static Task<float> ReadAsync(
        IDac dac, DacMeasureKind measure, float pressure, float tempC, string card, string channel, CancellationToken ct) =>
        measure switch
        {
            DacMeasureKind.UT => dac.ReadUtAsync(pressure, tempC, 5, card, channel, ct),
            DacMeasureKind.Usource => dac.ReadUsourceAsync(pressure, tempC, 5, card, channel, ct),
            DacMeasureKind.Isource => dac.ReadIsourceAsync(pressure, tempC, 5, card, channel, ct),
            DacMeasureKind.Usig => dac.ReadUsigAsync(pressure, tempC, 5, card, channel, ct),
            _ => Task.FromResult(float.NaN),
        };

    private static SlotEntry ResolveSlot(TaskContext ctx, int slotNum)
    {
        var name = $"Slot{slotNum}";
        var found = ctx.Slots.Entries.FirstOrDefault(s =>
            s.Slot.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            s.Slot.Equals(slotNum.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (found is not null) return found;

        var board = (slotNum - 1) / 16 + 1;
        var boardSlot = (slotNum - 1) % 16 + 1;
        return new SlotEntry(
            Slot: name,
            SerialNo: "-",
            Valve: ((slotNum - 1) / 32 + 1).ToString(CultureInfo.InvariantCulture),
            Board: board.ToString(CultureInfo.InvariantCulture),
            BoardSlotNo: boardSlot.ToString(CultureInfo.InvariantCulture),
            Layer: "1",
            Fixture: ((slotNum - 1) / 8 + 1).ToString(CultureInfo.InvariantCulture),
            FixtureSlotNo: ((slotNum - 1) % 8 + 1).ToString(CultureInfo.InvariantCulture),
            PressureController: "1",
            Dmm: "-",
            Channel: "-",
            ValveAddr: "-");
    }

    private static int GetDelayMs(IniFile settings, string key, int fallback) =>
        int.TryParse(settings.Get("DelaySettings", key, fallback.ToString(CultureInfo.InvariantCulture)), out var value)
            ? Math.Max(0, value)
            : fallback;
}
