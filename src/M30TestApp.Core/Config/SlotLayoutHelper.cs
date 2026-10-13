using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace M30TestApp.Core.Config;

public sealed record SlotLayoutOptions(
    int SlotCount,
    string BatchNo,
    int StartIndex,
    int StartBoard,
    int StartBoardSlot,
    int BoardSlotCapacity,
    int StartValve,
    int FixtureSlotCapacity,
    int FixtureCount,
    int StartChannel,
    int StartSerial,
    bool AutoNumber);

public static class SlotLayoutHelper
{
    public const int SlotMax = 256;

    /// <summary>每个阀门继电器负责的工位数（现场：每 32 个工位共用 1 个阀门）。</summary>
    public const int SlotsPerValve = 32;

    /// <summary>
    /// 工位号 → 阀门号（对应 Setting.ini [ValveSettings] 的 Valve{n}）。
    /// 每 32 个工位一个阀：1-32 → 阀1，33-64 → 阀2 …… 129-192 → 阀5/阀6，
    /// 即通道 106/107（现场按通道尾号口头称「6、7 号阀」）。
    /// 预览列表、配置页批量生成、运行时探漏与加压阀门切换必须共用本函数，
    /// 否则「按工位选的阀门」和「实际打开的阀门」会不一致。
    /// </summary>
    public static int ValveForSlot(int slotNo) =>
        (Math.Max(1, slotNo) - 1) / SlotsPerValve + 1;

    /// <summary>起始工位所在阀组的第一组工位号（用于展示「阀门 n - m」）。</summary>
    public static string ValveRangeText(int startSlotNo, int count)
    {
        var first = ValveForSlot(startSlotNo);
        var last = ValveForSlot(startSlotNo + Math.Max(0, count - 1));
        return first == last ? $"阀门 {first}" : $"阀门 {first} - {last}";
    }

    public static bool IsPlaceholderSerial(string? serialNo) =>
        string.IsNullOrWhiteSpace(serialNo)
        || serialNo.StartsWith("DEMO_", StringComparison.OrdinalIgnoreCase);

    public static int CountFilledSlots(IEnumerable<SlotEntry> entries) =>
        entries.Count(s => !IsPlaceholderSerial(s.SerialNo));

    public static Dictionary<string, string> CollectSerialMap(IEnumerable<SlotEntry> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in entries)
        {
            if (!IsPlaceholderSerial(s.SerialNo))
                map[s.Slot] = s.SerialNo;
        }
        return map;
    }

    public static void MergeSerialMaps(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (slot, serial) in source)
            target[slot] = serial;
    }

    /// <summary>收集已手动录入的芯片坐标（两项都空的行不收集）。</summary>
    public static Dictionary<string, (string X, string Y)> CollectPositions(IEnumerable<SlotEntry> entries)
    {
        var map = new Dictionary<string, (string X, string Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in entries)
        {
            if (string.IsNullOrWhiteSpace(s.PositionX) && string.IsNullOrWhiteSpace(s.PositionY)) continue;
            map[s.Slot] = (s.PositionX ?? "", s.PositionY ?? "");
        }
        return map;
    }

    public static void MergePositions(
        Dictionary<string, (string X, string Y)> target,
        IReadOnlyDictionary<string, (string X, string Y)> source)
    {
        foreach (var (slot, position) in source)
            target[slot] = position;
    }

    /// <summary>重新生成工位表后把之前录入的坐标按工位名回填。</summary>
    public static void ApplyPreservedPositions(
        IList<SlotEntry> slots,
        IReadOnlyDictionary<string, (string X, string Y)> preserved)
    {
        foreach (var slot in slots)
        {
            if (!preserved.TryGetValue(slot.Slot, out var position)) continue;
            if (!string.IsNullOrWhiteSpace(position.X)) slot.PositionX = position.X;
            if (!string.IsNullOrWhiteSpace(position.Y)) slot.PositionY = position.Y;
        }
    }

    /// <summary>从工位名解析工位号（"Slot12" → 12）；解析不出来返回 0。</summary>
    public static int ParseSlotNo(string? slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName)) return 0;
        var digits = new string(slotName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>连续区间生成（起始工位 + 数量），原有行为。</summary>
    public static List<SlotEntry> Generate(SlotLayoutOptions opt)
    {
        // 工位号 = 起始工位 + 序号，必须整体落在 1..SlotMax 内：
        // 起始工位 129 时最多只能生成 128 个工位（129-256）。
        var firstSlotNo = Math.Clamp(opt.StartIndex, 1, SlotMax);
        var count = Math.Clamp(opt.SlotCount, 1, SlotMax - firstSlotNo + 1);
        return Build(opt, Enumerable.Range(firstSlotNo, count).ToList());
    }

    /// <summary>
    /// 按**显式工位号列表**生成工位表，支持不连续（例如 1-8 与 10-16 混选）。
    ///
    /// 板卡 / 夹具 / 通道按「选定顺序」递增 —— 与连续生成时的行为完全一致，
    /// 因为物理接线是按夹具位置排的；阀门则始终按工位号推导（<see cref="ValveForSlot"/>）。
    /// </summary>
    public static List<SlotEntry> GenerateForSlots(SlotLayoutOptions opt, IEnumerable<int> slotNos)
    {
        var list = slotNos.Where(n => n >= 1 && n <= SlotMax).Distinct().OrderBy(n => n).ToList();
        return list.Count == 0 ? new List<SlotEntry>() : Build(opt, list);
    }

    private static List<SlotEntry> Build(SlotLayoutOptions opt, IReadOnlyList<int> slotNos)
    {
        var count = slotNos.Count;
        var list = new List<SlotEntry>(count);

        int board = opt.StartBoard;
        int boardSlot = opt.StartBoardSlot;
        int fixtureSlot = 1;
        int fixture = 1;
        int layer = 1;
        int channel = opt.StartChannel;
        int serial = opt.StartSerial;

        for (int i = 0; i < count; i++)
        {
            var slotNo = slotNos[i];
            string slotName = $"Slot{slotNo}";
            string serialNo = opt.AutoNumber
                ? $"{opt.BatchNo}#{serial}"
                : $"{opt.BatchNo}#{slotNo}";

            list.Add(new SlotEntry(
                Slot: slotName,
                SerialNo: serialNo,
                // 阀门严格按测试工位推导，不跟随「起始阀位」递增：
                // 起始工位 129 必须落在 5 号阀组，而不是从 1 号阀重新数。
                Valve: ValveForSlot(slotNo).ToString(CultureInfo.InvariantCulture),
                Board: board.ToString(),
                BoardSlotNo: boardSlot.ToString(),
                Layer: layer.ToString(),
                Fixture: fixture.ToString(),
                FixtureSlotNo: fixtureSlot.ToString(),
                PressureController: "1",
                Dmm: "-",
                Channel: channel.ToString(),
                ValveAddr: "-"));

            serial++;
            boardSlot++;
            if (boardSlot > opt.BoardSlotCapacity) { boardSlot = 1; board++; }
            fixtureSlot++;
            if (fixtureSlot > opt.FixtureSlotCapacity) { fixtureSlot = 1; fixture++; }
            if (fixture > opt.FixtureCount) { fixture = 1; layer++; }
            channel++;
        }

        return list;
    }

    // ─── 测试工位选择（支持不连续） ───────────────────────────────────────

    /// <summary>
    /// 解析工位选择文本。支持 <c>1-8,10-16</c>、单点 <c>3</c>，
    /// 逗号 / 中文逗号 / 顿号 / 分号 / 空格都能当分隔符；非法片段忽略，结果去重升序。
    /// </summary>
    public static List<int> ParseSlotSelection(string? text)
    {
        var set = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return new List<int>();

        var normalized = text
            .Replace('，', ',').Replace('、', ',').Replace('；', ',').Replace(';', ',');
        foreach (var raw in normalized.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                var a = part[..dash].Trim();
                var b = part[(dash + 1)..].Trim();
                if (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)) continue;
                if (!int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to)) continue;
                if (from > to) (from, to) = (to, from);
                for (var n = Math.Max(1, from); n <= Math.Min(SlotMax, to); n++) set.Add(n);
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
                     && single >= 1 && single <= SlotMax)
            {
                set.Add(single);
            }
        }
        return set.ToList();
    }

    /// <summary>把工位号列表压成紧凑区间文本：<c>1-8,10-16</c>。</summary>
    public static string FormatSlotSelection(IEnumerable<int> slotNos)
    {
        var list = slotNos.Where(n => n >= 1 && n <= SlotMax).Distinct().OrderBy(n => n).ToList();
        if (list.Count == 0) return "";

        var parts = new List<string>();
        var start = list[0];
        var prev = list[0];
        for (var i = 1; i <= list.Count; i++)
        {
            var cur = i < list.Count ? list[i] : int.MinValue;
            if (cur == prev + 1) { prev = cur; continue; }
            parts.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture)
                                    : $"{start}-{prev}");
            start = prev = cur;
        }
        return string.Join(",", parts);
    }

    public static void ApplyPreservedSerials(IList<SlotEntry> slots, IReadOnlyDictionary<string, string> preserved)
    {
        foreach (var slot in slots)
        {
            if (preserved.TryGetValue(slot.Slot, out var serial) && !string.IsNullOrWhiteSpace(serial))
                slot.SerialNo = serial;
        }
    }

    public static List<SlotEntry> TrimTrailingPlaceholders(IReadOnlyList<SlotEntry> slots)
    {
        var last = slots.Count - 1;
        while (last >= 0 && IsPlaceholderSerial(slots[last].SerialNo))
            last--;
        if (last < 0) return new List<SlotEntry>();
        return slots.Take(last + 1).ToList();
    }
}
