using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace M30TestApp.Core.Config;

/// <summary>点位批量录入对话框的一行规则输入。</summary>
public readonly record struct PointBatchRow(string Range, string Value, string Extra);

/// <summary>
/// 测试方案点位（压力点/温度点）批量录入解析器。
/// 从 ConfigViewModel 下沉的纯逻辑，便于单元测试与跨 VM 复用。
///
 /// 结构化规则格式（Range=Value[,Extra]）：
///   "1-20=100"        → P1..P20 全部 100
///   "5=23.8,绝压"     → P5 = 23.8 且标记绝压
///   温度点 Extra 为保温分钟数："3=85,120" → T3 = 85℃ 保温 120min
/// </summary>
public static class PointBatchParser
{
    /// <summary>把结构化规则行解析为带序号的点位集合（按序号升序）。</summary>
    public static IReadOnlyList<(int Index, float Value, string Extra, string Source)> ParseRows(
        IEnumerable<PointBatchRow> rows, string prefix)
    {
        var inputs = rows
            .Select(r => new PointBatchRow(r.Range.Trim(), r.Value.Trim(), r.Extra.Trim()))
            .Where(r => !string.IsNullOrWhiteSpace(r.Range) ||
                        !string.IsNullOrWhiteSpace(r.Value) ||
                        !string.IsNullOrWhiteSpace(r.Extra))
            .ToList();
        if (inputs.Count == 0)
            return Array.Empty<(int, float, string, string)>();

        var singleInput = inputs.Count == 1;
        var values = new SortedDictionary<int, (float Value, string Extra, string Source)>();
        foreach (var row in inputs)
        {
            var source = $"{row.Range} / {row.Value} / {row.Extra}".Trim();
            if (string.IsNullOrWhiteSpace(row.Range))
                throw new FormatException($"范围/序号不能为空：{source}");
            if (string.IsNullOrWhiteSpace(row.Value))
                throw new FormatException($"数值不能为空：{source}");

            var value = ParseFloat(row.Value, source);
            var indexes = ExpandIndexes(row.Range, source, singleInput).ToList();
            if (indexes.Count == 0)
                throw new FormatException($"无法识别点位范围：{source}");

            foreach (var index in indexes)
                values[index] = (value, row.Extra, source);
        }

        return values
            .Select(kv => (kv.Key, kv.Value.Value, kv.Value.Extra, kv.Value.Source))
            .ToList();
    }

    public static IReadOnlyList<PressurePoint> BuildPressurePoints(
        IEnumerable<PointBatchRow> rows, PressureType defaultPressureType)
    {
        return ParseRows(rows, "P")
            .Select(p => new PressurePoint(
                $"P{p.Index}", p.Value, ResolvePressureType(p.Extra, defaultPressureType)))
            .ToList();
    }

    public static IReadOnlyList<TempPoint> BuildTempPoints(IEnumerable<PointBatchRow> rows)
    {
        return ParseRows(rows, "T")
            .Select(p => new TempPoint($"T{p.Index}", p.Value, ParseSoakMinutes(p.Extra, p.Source)))
            .ToList();
    }

    private static IEnumerable<int> ExpandIndexes(string text, string line, bool singleInput)
    {
        try
        {
            return PointBatchRangeParser.ExpandIndexes(text, singleInput).ToList();
        }
        catch (FormatException ex)
        {
            throw new FormatException($"{ex.Message}（{line}）", ex);
        }
    }

    private static int? ParseSoakMinutes(string text, string line)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = NormalizeLine(text);
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            return minutes;
        throw new FormatException($"保温时间请输入整数分钟：{line}");
    }

    /// <summary>从点位名（如 "P12"/"T3"）提取序号文本；不匹配时返回 fallback。</summary>
    public static string IndexFromName(string name, string prefix, int fallback)
    {
        var match = Regex.Match(name ?? "", $@"^{Regex.Escape(prefix)}(?<index>\d+)$", RegexOptions.IgnoreCase);
        return match.Success
            ? match.Groups["index"].Value
            : fallback.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把用户输入的压力类型显示文本（绝压/Absolute/ABS…）解析为枚举，无法识别时返回 fallback。</summary>
    public static PressureType ResolvePressureType(string? text, PressureType fallback)
    {
        var normalized = StripComboPrefix(text);
        if (string.IsNullOrWhiteSpace(normalized)) return fallback;
        if (normalized.Contains("绝压", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Absolute", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ABS", StringComparison.OrdinalIgnoreCase))
            return PressureType.Absolute;
        if (normalized.Contains("差压", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Differential", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("DIFF", StringComparison.OrdinalIgnoreCase))
            return PressureType.Differential;
        if (normalized.Contains("表压", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Gauge", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("GAUG", StringComparison.OrdinalIgnoreCase))
            return PressureType.Gauge;
        return fallback;
    }

    /// <summary>枚举 → 中文显示名。</summary>
    public static string PressureTypeToDisplay(PressureType pressureType) => pressureType switch
    {
        PressureType.Absolute     => "绝压",
        PressureType.Differential => "差压",
        _                         => "表压",
    };

    /// <summary>去掉下拉项可能的 "前缀:" 部分。</summary>
    public static string StripComboPrefix(string? text)
    {
        var normalized = (text ?? "").Trim();
        var colon = normalized.LastIndexOf(':');
        return colon >= 0 ? normalized[(colon + 1)..].Trim() : normalized;
    }

    private static string NormalizeLine(string line) =>
        line.Replace('，', ',')
            .Replace('：', ':')
            .Replace("℃", "")
            .Replace("°C", "", StringComparison.OrdinalIgnoreCase)
            .Replace("摄氏度", "")
            .Replace("kPa", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MPa", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Pa", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static float ParseFloat(string value, string line)
    {
        var normalized = NormalizeLine(value);
        if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        throw new FormatException($"无法解析数值：{line}");
    }
}
