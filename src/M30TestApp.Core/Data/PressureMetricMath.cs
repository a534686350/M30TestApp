using System.Linq;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Data;

public static class PressureMetricMath
{
    public static double Span(double lowSig, double highSig, double lowP, double highP, PressureType pressureType)
    {
        if (Invalid(lowSig, highSig, lowP, highP)) return -999;

        var measuredPressureSpan = highP - lowP;
        var measuredSignalSpan = highSig - lowSig;
        if (pressureType != PressureType.Absolute)
            return measuredSignalSpan;

        if (measuredPressureSpan == 0) return -998;
        return measuredSignalSpan / measuredPressureSpan * highP;
    }

    public static double LinearityError(
        double midSig,
        double lowSig,
        double highSig,
        double highP,
        double lowP,
        double midP,
        PressureType pressureType)
    {
        if (Invalid(midSig, lowSig, highSig, highP, lowP, midP)) return -999;

        var measuredPressureSpan = highP - lowP;
        var measuredSignalSpan = highSig - lowSig;
        if (measuredPressureSpan == 0 || measuredSignalSpan == 0) return -998;

        var slope = measuredSignalSpan / measuredPressureSpan;
        var ideal = slope * (midP - lowP) + lowSig;
        var denominator = pressureType == PressureType.Absolute
            ? measuredSignalSpan / measuredPressureSpan * highP
            : measuredSignalSpan;
        if (denominator == 0) return -998;

        return (midSig - ideal) * 100 / denominator;
    }

    private static bool Invalid(params double[] values) => values.Any(v => double.IsNaN(v) || v <= -998);
}
