using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Data;

public enum LongTermColumnKind { Voltage, Resistance, OvenTemp }

/// <summary>
/// One column in the long-term stability matrix (DMM value or oven temp °C).
/// </summary>
public sealed class LongTermMatrixColumn
{
    public required string Key { get; init; }
    public required string TempPointName { get; init; }
    public required string TempLabel { get; init; }
    public required string SubLabel { get; init; }
    public required LongTermColumnKind Kind { get; init; }

    public string FlatHeader => $"{TempLabel}/{SubLabel}";
}

/// <summary>
/// Builds column layout: for each temperature point → all pressure DMM columns + one oven temp.
/// </summary>
public static class LongTermStabilityMatrix
{
    public static string DmmKey(TempPoint tp, PressurePoint pp, LongTermStabilityMeasureMode mode) =>
        mode == LongTermStabilityMeasureMode.Resistance
            ? $"{tp.Name}_{pp.Name}_R"
            : $"{tp.Name}_{pp.Name}_V";

    public static string TempKey(TempPoint tp) => $"{tp.Name}_T";

    public static IReadOnlyList<LongTermMatrixColumn> BuildColumns(
        TestPlan plan, LongTermStabilityMeasureMode mode = LongTermStabilityMeasureMode.Voltage)
    {
        var unit = string.IsNullOrWhiteSpace(plan.PressureUnit) ? "kPa" : plan.PressureUnit;
        var dmmKind = mode == LongTermStabilityMeasureMode.Resistance
            ? LongTermColumnKind.Resistance
            : LongTermColumnKind.Voltage;
        var list = new List<LongTermMatrixColumn>();
        foreach (var tp in plan.TempPoints)
        {
            var tempLabel = FormatTempHeader(tp);
            foreach (var pp in plan.PressurePoints)
            {
                list.Add(new LongTermMatrixColumn
                {
                    Key = DmmKey(tp, pp, mode),
                    TempPointName = tp.Name,
                    TempLabel = tempLabel,
                    SubLabel = FormatPressure(pp, unit),
                    Kind = dmmKind,
                });
            }
            list.Add(new LongTermMatrixColumn
            {
                Key = TempKey(tp),
                TempPointName = tp.Name,
                TempLabel = tempLabel,
                SubLabel = "Temp",
                Kind = LongTermColumnKind.OvenTemp,
            });
        }
        return list;
    }

    public static IReadOnlyDictionary<string, string> BuildFlatHeaderMap(
        TestPlan plan, LongTermStabilityMeasureMode mode = LongTermStabilityMeasureMode.Voltage) =>
        BuildColumns(plan, mode).ToDictionary(c => c.Key, c => c.FlatHeader, StringComparer.OrdinalIgnoreCase);

    public static string MatrixTitle(LongTermStabilityMeasureMode mode) =>
        mode == LongTermStabilityMeasureMode.Resistance
            ? "DAQ973A 电阻数据矩阵 (Ω)"
            : "DAQ973A 电压数据矩阵 (mV)";

    private static string FormatTempHeader(TempPoint tp)
    {
        var celsius = tp.Celsius.ToString("0.##", CultureInfo.InvariantCulture) + "°C";
        return string.IsNullOrWhiteSpace(tp.Name) ? celsius : $"{tp.Name} {celsius}";
    }

    private static string FormatPressure(PressurePoint pp, string unit)
    {
        var value = pp.Value.ToString("0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? value : value + unit;
    }
}
