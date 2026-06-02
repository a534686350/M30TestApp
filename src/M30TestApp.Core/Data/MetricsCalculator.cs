using System;
using System.Globalization;
using System.Linq;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Data;

public static class MetricsCalculator
{
    public static int Calculate(TaskContext ctx)
    {
        if (ctx.Plan.TempPoints.Count < 3 || ctx.Plan.PressurePoints.Count < 3)
        {
            AppLog.Warn("Cal", "指标计算需要至少 3 个温度点和 3 个压力点，已跳过");
            return 0;
        }

        var temps = ctx.Plan.TempPoints.ToArray();
        var pressures = ctx.Plan.PressurePoints.ToArray();
        var t1 = temps[0];
        var t2 = temps[1];
        var t3 = temps[2];
        var p0 = pressures[0];
        var p50 = pressures[pressures.Length / 2];
        var p100 = pressures[^1];
        var count = 0;

        foreach (var slot in ctx.Slots.Entries)
        {
            var slotName = slot.Slot;
            var usgP0T1 = Get(ctx, slotName, Usg(t1, p0));
            var usgP50T1 = Get(ctx, slotName, Usg(t1, p50));
            var usgP100T1 = Get(ctx, slotName, Usg(t1, p100));
            var uscT1 = Get(ctx, slotName, Usc(t1));
            var iscT1 = Get(ctx, slotName, Isc(t1));

            var usgP0T2 = Get(ctx, slotName, Usg(t2, p0));
            var usgP100T2 = Get(ctx, slotName, Usg(t2, p100));
            var utT2 = Get(ctx, slotName, Ut(t2));
            var tempT2 = Get(ctx, slotName, OvenTemp(t2));
            var uscT2 = Get(ctx, slotName, Usc(t2));
            var iscT2 = Get(ctx, slotName, Isc(t2));

            var usgP0T3 = Get(ctx, slotName, Usg(t3, p0));
            var usgP50T3 = Get(ctx, slotName, Usg(t3, p50));
            var usgP100T3 = Get(ctx, slotName, Usg(t3, p100));
            var utT3 = Get(ctx, slotName, Ut(t3));
            var tempT3 = Get(ctx, slotName, OvenTemp(t3));
            var uscT3 = Get(ctx, slotName, Usc(t3));
            var iscT3 = Get(ctx, slotName, Isc(t3));

            var rb5T1 = Rb(uscT1, iscT1);
            var rb5T2 = Rb(uscT2, iscT2);
            var rb5T3 = Rb(uscT3, iscT3);
            Set(ctx, slotName, "Rb5_T1", rb5T1, null);
            Set(ctx, slotName, "Rb5_T2", rb5T2, null);
            Set(ctx, slotName, "Rb5_T3", rb5T3, null);

            var offset = usgP0T3;
            var span = Sub(usgP100T3, usgP0T3);
            var nl = LinearityError(usgP50T3, usgP0T3, usgP100T3, p100.Value, p0.Value, p50.Value);
            var tco = Tco(usgP0T2, usgP0T3, tempT2, tempT3, usgP100T3);
            var tcs = Tcs(usgP100T2, usgP0T2, usgP100T3, usgP0T3, tempT2, tempT3);
            var tcr = Tcr(rb5T2, rb5T3, tempT2, tempT3);
            var tho = Tho(usgP0T3, usgP0T1, usgP100T3);
            var ths = Ths(usgP100T3, usgP0T3, usgP100T1, usgP0T1);
            var usgP0T3R = Get(ctx, slotName, $"{t3.Name}{p0.Name}_USG_R");
            var ph = Ph(usgP0T3R, usgP0T3, usgP100T3);
            var tct = Tct(utT2, utT3, tempT2, tempT3);

            Set(ctx, slotName, "Offset", offset, SpecFor(ctx, "Offset", ctx.Plan.Specs.Offset));
            Set(ctx, slotName, "Span", span, SpecFor(ctx, "Span", ctx.Plan.Specs.Span));
            Set(ctx, slotName, "NL", nl, SpecFor(ctx, "NL", ctx.Plan.Specs.Linearity));
            Set(ctx, slotName, "TCO", tco, SpecFor(ctx, "TCO", ctx.Plan.Specs.TCO));
            Set(ctx, slotName, "TCS", tcs, SpecFor(ctx, "TCS", ctx.Plan.Specs.TCS));
            Set(ctx, slotName, "TCR", tcr, SpecFor(ctx, "TCR", ctx.Plan.Specs.TCR));
            Set(ctx, slotName, "THO", tho, SpecFor(ctx, "THO", ctx.Plan.Specs.THO));
            Set(ctx, slotName, "THS", ths, SpecFor(ctx, "THS", ctx.Plan.Specs.THS));
            Set(ctx, slotName, "PH", ph, SpecFor(ctx, "PH", ctx.Plan.Specs.PressureHysteresis));
            Set(ctx, slotName, "TCT", tct, SpecFor(ctx, "TCT", ctx.Plan.Specs.CT));
            count++;
        }

        return count;
    }

    private static string Usg(TempPoint t, PressurePoint p) => $"{t.Name}{p.Name}_USG";
    private static string Usc(TempPoint t) => $"{t.Name}_USC";
    private static string Isc(TempPoint t) => $"{t.Name}_ISC";
    private static string Ut(TempPoint t) => $"{t.Name}_UT";
    private static string OvenTemp(TempPoint t) => $"{t.Name}_OvenTemp";

    private static double Get(TaskContext ctx, string slot, string key)
    {
        var cell = ctx.Matrix.Get(slot, DataMatrix.SanitizeKey(key));
        if (cell is null) return double.NaN;
        return double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private static SpecRange? SpecFor(TaskContext ctx, string metricCode, SpecRange spec) =>
        ctx.Plan.IsMetricEnabled(metricCode) ? spec : null;

    private static void Set(TaskContext ctx, string slot, string key, double value, SpecRange? spec)
    {
        var status = double.IsNaN(value) || value <= -998
            ? CellStatus.Error
            : spec is { HasLimits: true } && !spec.IsInRange(value)
                ? CellStatus.Warn
                : CellStatus.Ok;
        ctx.Matrix.Set(slot, key, value, status);
        if (!ctx.Columns.Contains(key)) ctx.Columns.Add(key);
    }

    private static double Rb(double uSource, double iSource)
    {
        if (Invalid(uSource, iSource)) return -999;
        if (iSource == 0) return -998;
        return uSource / iSource;
    }

    private static double Sub(double a, double b) => Invalid(a, b) ? -999 : a - b;

    private static double LinearityError(double midSig, double lowSig, double highSig, double highP, double lowP, double midP)
    {
        if (Invalid(midSig, lowSig, highSig, highP, lowP, midP)) return -999;
        if (highP - lowP == 0 || highSig - lowSig == 0) return -998;
        var slope = (highSig - lowSig) / (highP - lowP);
        return (midSig - (slope * (midP - lowP) + lowSig)) * 100 / (highSig - lowSig);
    }

    private static double Tco(double p0T2, double p0T3, double tempT2, double tempT3, double p100T3)
    {
        if (Invalid(p0T2, p0T3, tempT2, tempT3, p100T3)) return -999;
        if (tempT2 - tempT3 == 0 || p100T3 - p0T3 == 0) return -998;
        return (p0T2 - p0T3) / (tempT2 - tempT3) * 100 / (p100T3 - p0T3);
    }

    private static double Tcs(double p100T2, double p0T2, double p100T3, double p0T3, double tempT2, double tempT3)
    {
        if (Invalid(p100T2, p0T2, p100T3, p0T3, tempT2, tempT3)) return -999;
        if (p100T3 - p0T3 == 0 || tempT2 - tempT3 == 0) return -998;
        return (p100T2 - p0T2 - (p100T3 - p0T3)) / (tempT2 - tempT3) * 100 / (p100T3 - p0T3);
    }

    private static double Tcr(double rbT2, double rbT3, double tempT2, double tempT3)
    {
        if (Invalid(rbT2, rbT3, tempT2, tempT3)) return -999;
        if (rbT3 == 0 || tempT2 - tempT3 == 0) return -998;
        return (rbT2 - rbT3) / (tempT2 - tempT3) * 100 / rbT3;
    }

    private static double Tho(double p0T3, double p0T1, double p100T3)
    {
        if (Invalid(p0T3, p0T1, p100T3)) return -999;
        if (p100T3 - p0T3 == 0) return -998;
        return (p0T3 - p0T1) * 100 / (p100T3 - p0T3);
    }

    private static double Ths(double p100T3, double p0T3, double p100T1, double p0T1)
    {
        if (Invalid(p100T3, p0T3, p100T1, p0T1)) return -999;
        if (p100T3 - p0T3 == 0) return -998;
        return (p100T3 - p0T3 - (p100T1 - p0T1)) * 100 / (p100T3 - p0T3);
    }

    private static double Ph(double p0T3End, double p0T3Start, double p100T3)
    {
        if (Invalid(p0T3End, p0T3Start, p100T3)) return -999;
        if (p100T3 - p0T3End == 0) return -998;
        return (p0T3End - p0T3Start) * 100 / (p100T3 - p0T3End);
    }

    private static double Tct(double utT2, double utT3, double tempT2, double tempT3)
    {
        if (Invalid(utT2, utT3, tempT2, tempT3)) return -999;
        if (tempT2 - tempT3 == 0) return -998;
        return (utT2 - utT3) / (tempT2 - tempT3);
    }

    private static bool Invalid(params double[] values) => values.Any(v => double.IsNaN(v) || v <= -998);
}
