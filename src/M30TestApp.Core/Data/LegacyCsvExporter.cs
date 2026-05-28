using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Data;

/// <summary>
/// 按照旧版 WinForms ASLab 程序的 CSV 格式导出测试数据。
/// 生成两份文件：
///   1. 完整内部数据 CSV（所有原始采集 + 计算指标）
///   2. 客户用测试报告 CSV（仅计算指标 + 关键参数）
///
/// 仅适用于 M30 标准测试方案（≥3温度点 × ≥3压力点）。
/// </summary>
public static class LegacyCsvExporter
{
    /// <summary>
    /// 判断当前方案是否属于 M30 标准测试（可使用旧版格式导出）。
    /// 条件：方案文件夹为"M30测试"，且至少 3 个温度点、3 个压力点。
    /// </summary>
    public static bool IsLegacyProfile(TestPlan plan) =>
        string.Equals(plan.FolderName, "M30测试", StringComparison.OrdinalIgnoreCase)
        && plan.TempPoints.Count >= 3
        && plan.PressurePoints.Count >= 3;

    /// <summary>
    /// 导出两份旧版格式 CSV 到桌面。
    /// </summary>
    public static void Export(TaskContext ctx)
    {
        if (!IsLegacyProfile(ctx.Plan))
        {
            AppLog.Warn("LegacyCSV", "方案不满足旧版格式条件（需≥3温度点×3压力点），跳过旧版导出");
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var now = DateTime.Now;
        var timeStr = now.ToString("yyyy-MM-dd HH时mm分ss秒");
        var sensorType = string.IsNullOrWhiteSpace(ctx.Plan.SensorType) ? ctx.Plan.Name : ctx.Plan.SensorType;

        // 1. 完整内部 CSV
        var fullPath = Path.Combine(desktop, $"{timeStr}_{sensorType}.csv");
        ExportFull(ctx, fullPath, now);
        AppLog.Info("LegacyCSV", $"完整数据已保存到 {fullPath}");

        // 2. 客户用测试报告 CSV
        var customerPath = Path.Combine(desktop, $"客户用测试报告{timeStr}_{sensorType}.csv");
        ExportCustomer(ctx, customerPath, now);
        AppLog.Info("LegacyCSV", $"客户报告已保存到 {customerPath}");
    }

    /// <summary>完整内部 CSV：表头列 + 计算指标 + 全部原始测量列。</summary>
    private static void ExportFull(TaskContext ctx, string path, DateTime now)
    {
        var plan = ctx.Plan;
        var temps = plan.TempPoints.ToArray();
        var pressures = plan.PressurePoints.ToArray();
        var endTime = now.ToString("s"); // ISO 8601 sortable

        // 构建列定义（按旧版顺序）
        var columns = BuildFullColumns(plan);

        // 编码与旧版一致：系统默认 ANSI
        using var sw = new StreamWriter(path, false, Encoding.Default);

        // 写表头
        sw.WriteLine(string.Join(",", columns.Select(c => c.Header)));

        // 每个工位一行
        foreach (var slot in ctx.Slots.Entries)
        {
            var values = new List<string>(columns.Count);
            foreach (var col in columns)
            {
                values.Add(col.GetValue(ctx, slot, now, endTime));
            }
            sw.WriteLine(string.Join(",", values));
        }
    }

    /// <summary>客户用 CSV：仅 17 列精选指标。</summary>
    private static void ExportCustomer(TaskContext ctx, string path, DateTime now)
    {
        using var sw = new StreamWriter(path, false, Encoding.Default);

        // 固定 17 列表头
        var headers = new[]
        {
            "SensorType", "SerialNo", "TestTime",
            "Rb12 [KOhm]", "Rb5 T1 [KOhm]", "Rb5 T2 [KOhm]", "Rb5 T3 [KOhm]",
            "Offset [mV]", "Span [mV]", "NL [%FSS]",
            "TCO [%FSS/K]", "TCS [%FSS/K]", "TCR [%/K]",
            "THO [%FSS]", "THS [%FSS]", "PH [%FSS]", "TCT"
        };
        sw.WriteLine(string.Join(",", headers));

        // 对应的矩阵 key
        var metricKeys = new[]
        {
            "Rb5_T1", "Rb5_T2", "Rb5_T3",
            "Offset", "Span", "NL",
            "TCO", "TCS", "TCR",
            "THO", "THS", "PH", "TCT"
        };

        foreach (var slot in ctx.Slots.Entries)
        {
            var vals = new List<string>
            {
                ctx.Plan.SensorType,
                slot.SerialNo,
                now.ToString("yyyy-MM-dd"),
            };
            foreach (var key in metricKeys)
            {
                vals.Add(GetCellValue(ctx, slot.Slot, key));
            }
            sw.WriteLine(string.Join(",", vals));
        }
    }

    // ─── 列定义 ──────────────────────────────────────────────────────────

    private sealed class ColumnDef
    {
        public string Header { get; init; } = "";
        public Func<TaskContext, SlotEntry, DateTime, string, string> GetValue { get; init; } = (_, _, _, _) => "";
    }

    private static List<ColumnDef> BuildFullColumns(TestPlan plan)
    {
        var cols = new List<ColumnDef>();
        var temps = plan.TempPoints.ToArray();
        var pressures = plan.PressurePoints.ToArray();

        // ── 元信息列 ──
        cols.Add(new ColumnDef { Header = "SensorType", GetValue = (ctx, s, _, _) => ctx.Plan.SensorType });
        cols.Add(new ColumnDef { Header = "SerialNo", GetValue = (_, s, _, _) => s.SerialNo });
        cols.Add(new ColumnDef { Header = "SlotNo", GetValue = (_, s, _, _) => s.Slot });
        cols.Add(new ColumnDef { Header = "SlotAddr1", GetValue = (_, s, _, _) => s.Board });
        cols.Add(new ColumnDef { Header = "SlotAddr2", GetValue = (_, s, _, _) => s.BoardSlotNo });
        cols.Add(new ColumnDef { Header = "TestStartTime", GetValue = (_, _, t, _) => t.AddHours(-2).ToString("s") });
        cols.Add(new ColumnDef { Header = "TestEndTime", GetValue = (_, _, _, e) => e });
        cols.Add(new ColumnDef { Header = "TestResult", GetValue = (ctx, s, _, _) => GetTestResult(ctx, s.Slot) });

        // ── 压力点值 ──
        var pLabels = new[] { "P0", "P50", "P100" };
        for (var i = 0; i < 3 && i < pressures.Length; i++)
        {
            var pVal = pressures[i].Value.ToString(CultureInfo.InvariantCulture);
            cols.Add(new ColumnDef { Header = pLabels[i], GetValue = (_, _, _, _) => pVal });
        }
        cols.Add(new ColumnDef { Header = "PUnit", GetValue = (ctx, _, _, _) => ctx.Plan.PressureUnit });

        // ── 计算指标 ──
        cols.Add(MetricCol("Rb12_T1"));
        cols.Add(MetricCol("Rb5_T1"));
        cols.Add(MetricCol("Rb5_T2"));
        cols.Add(MetricCol("Rb5_T3"));
        cols.Add(MetricCol("Offset"));
        cols.Add(MetricCol("Span"));
        cols.Add(MetricCol("LinearityError", "NL"));
        cols.Add(MetricCol("TCO"));
        cols.Add(MetricCol("TCS"));
        cols.Add(MetricCol("TCR"));
        cols.Add(MetricCol("THO"));
        cols.Add(MetricCol("THS"));
        cols.Add(MetricCol("PressureHysteresis", "PH"));
        cols.Add(MetricCol("TCT"));

        // ── 稳定性列（占位） ──
        cols.Add(EmptyCol("StabilityOffset2H"));
        cols.Add(EmptyCol("StabilityOffset26H"));
        cols.Add(EmptyCol("StabilityDeltaOffset"));
        cols.Add(EmptyCol("StabilityTHO"));

        // ── 原始测量列 ──
        // 旧版格式: {Measure}_{Pressure}_{Temp}_{Voltage}_{PointIndex}
        // V2 矩阵列: {TempName}{PressureName}_{MeasureType} 或 {TempName}_{MeasureType}
        var pointIndex = 0;

        for (var ti = 0; ti < temps.Length && ti < 3; ti++)
        {
            var tp = temps[ti];
            var tLabel = $"T{ti + 1}";

            // 第一个温度点有 V12 初始测量
            if (ti == 0)
            {
                pointIndex++;
                var idx = pointIndex;
                AddMeasurementGroup(cols, "P0", tLabel, "V12", idx, tp, pressures[0]);
            }

            // 每个温度点：P0→P50→P100→P50→P0 循环（V5）
            // 上行: P0, P50(mid), P100
            for (var pi = 0; pi < pressures.Length && pi < 3; pi++)
            {
                pointIndex++;
                var pLabel = pi switch { 0 => "P0", 1 => "P50", 2 => "P100", _ => $"P{pi}" };
                var idx = pointIndex;
                AddMeasurementGroup(cols, pLabel, tLabel, "V5", idx, tp, pressures[pi]);
            }

            // 下行: P50, P0（迟滞测量）—— 使用 _USG_R 回差列
            if (pressures.Length >= 2)
            {
                pointIndex++;
                AddMeasurementGroup(cols, "P50", tLabel, "V5", pointIndex, tp, pressures[pressures.Length / 2], "_USG_R");
            }
            {
                pointIndex++;
                AddMeasurementGroup(cols, "P0", tLabel, "V5", pointIndex, tp, pressures[0], "_USG_R");
            }

            // T2 24H 稳定性（占位）
            if (ti == 1)
            {
                cols.Add(EmptyCol($"Usig_P0_{tLabel}_V5_24H"));
            }
        }

        return cols;
    }

    private static void AddMeasurementGroup(
        List<ColumnDef> cols, string pLabel, string tLabel, string vLabel, int idx,
        TempPoint tp, PressurePoint pp, string usgSuffix = "_USG")
    {
        // 旧版列名: Usource_P0_T1_V5_1
        // 对应V2矩阵列: T1_USC (无压力时) 或 T1P1_USG (有压力时)
        var legacyPrefix = $"{pLabel}_{tLabel}_{vLabel}_{idx}";

        // Usource → V2: {TempName}_USC
        cols.Add(new ColumnDef
        {
            Header = $"Usource_{legacyPrefix}",
            GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, $"{tp.Name}_USC")
        });

        // Isource → V2: {TempName}_ISC
        cols.Add(new ColumnDef
        {
            Header = $"Isource_{legacyPrefix}",
            GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, $"{tp.Name}_ISC")
        });

        // Usig → V2: {TempName}{PressureName}{usgSuffix}
        cols.Add(new ColumnDef
        {
            Header = $"Usig_{legacyPrefix}",
            GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, $"{tp.Name}{pp.Name}{usgSuffix}")
        });

        // T (烘箱温度) → V2: {TempName}_OvenTemp
        cols.Add(new ColumnDef
        {
            Header = $"T_{legacyPrefix}",
            GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, $"{tp.Name}_OvenTemp")
        });

        // UT → V2: {TempName}_UT
        cols.Add(new ColumnDef
        {
            Header = $"UT_{legacyPrefix}",
            GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, $"{tp.Name}_UT")
        });
    }

    private static ColumnDef MetricCol(string header, string? matrixKey = null) => new()
    {
        Header = header,
        GetValue = (ctx, s, _, _) => GetCellValue(ctx, s.Slot, matrixKey ?? header)
    };

    private static ColumnDef EmptyCol(string header) => new()
    {
        Header = header,
        GetValue = (_, _, _, _) => ""
    };

    // ─── 辅助 ──────────────────────────────────────────────────────────────

    private static string GetCellValue(TaskContext ctx, string slot, string key)
    {
        var cell = ctx.Matrix.Get(slot, DataMatrix.SanitizeKey(key));
        return cell?.Value ?? "";
    }

    /// <summary>
    /// 判断工位测试结果：检查所有已启用指标是否在限值范围内。
    /// </summary>
    private static string GetTestResult(TaskContext ctx, string slot)
    {
        var specs = ctx.Plan.Specs;
        var checks = new (string Code, SpecRange Spec)[]
        {
            ("Offset", specs.Offset), ("Span", specs.Span), ("NL", specs.Linearity),
            ("TCO", specs.TCO), ("TCS", specs.TCS), ("TCR", specs.TCR),
            ("THO", specs.THO), ("THS", specs.THS), ("PH", specs.PressureHysteresis),
            ("TCT", specs.CT),
        };

        var anyFail = false;
        foreach (var (code, spec) in checks)
        {
            if (!ctx.Plan.IsMetricEnabled(code)) continue;
            if (!spec.HasLimits) continue;
            var val = GetCellValue(ctx, slot, code);
            if (string.IsNullOrEmpty(val)) continue;
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !spec.IsInRange(v))
            {
                anyFail = true;
                break;
            }
        }

        return anyFail ? "fail" : "pass";
    }
}
