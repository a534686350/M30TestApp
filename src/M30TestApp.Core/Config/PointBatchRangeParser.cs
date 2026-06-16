using System.Globalization;
using System.Text.RegularExpressions;

namespace M30TestApp.Core.Config;

/// <summary>
/// Parses point index ranges for batch temp/pressure entry.
/// Supports: <c>20</c> (count from 1), <c>1 20</c> (start + count), <c>1-20</c>, <c>T1-T20</c>.
/// </summary>
public static class PointBatchRangeParser
{
    private static readonly Regex BareCountRegex = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex StartCountRegex = new(
        @"^(?:[PT])?(?<start>\d+)\s+(?<count>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RangeTokenRegex = new(
        @"^(?:[PT])?(?<start>\d+)(?:[-~至到](?:[PT])?(?<end>\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Normalize(string text) =>
        Regex.Replace(
                (text ?? "")
                    .Replace('，', ',')
                    .Replace('：', ':')
                    .Trim(),
                @"\s*([-~至到])\s*",
                "$1")
            .Trim();

    public static IEnumerable<int> ExpandIndexes(string text, bool allowBareCount)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        if (allowBareCount && BareCountRegex.IsMatch(normalized) &&
            int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bareCount))
        {
            if (bareCount <= 0)
                throw new FormatException($"点位数量必须大于 0：{text}");
            foreach (var i in Enumerable.Range(1, bareCount))
                yield return i;
            yield break;
        }

        var startCount = StartCountRegex.Match(normalized);
        if (startCount.Success)
        {
            var start = int.Parse(startCount.Groups["start"].Value, CultureInfo.InvariantCulture);
            var count = int.Parse(startCount.Groups["count"].Value, CultureInfo.InvariantCulture);
            if (start <= 0 || count <= 0)
                throw new FormatException($"起始序号和数量必须大于 0：{text}");
            foreach (var i in Enumerable.Range(start, count))
                yield return i;
            yield break;
        }

        var tokens = normalized.Split(
            new[] { ',', ';', '；', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            yield break;

        foreach (var token in tokens)
        {
            var match = RangeTokenRegex.Match(token);
            if (!match.Success)
                throw new FormatException($"无法识别点位范围：{text}");

            var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            var end = match.Groups["end"].Success
                ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)
                : start;
            if (start <= 0 || end <= 0)
                throw new FormatException($"点位序号必须大于 0：{text}");

            var step = start <= end ? 1 : -1;
            for (var i = start; ; i += step)
            {
                yield return i;
                if (i == end) break;
            }
        }
    }

    public static bool TryCount(string text, bool allowBareCount, out int count)
    {
        count = 0;
        try
        {
            var indexes = ExpandIndexes(text, allowBareCount).Distinct().ToList();
            if (indexes.Count == 0)
                return false;
            count = indexes.Count;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
