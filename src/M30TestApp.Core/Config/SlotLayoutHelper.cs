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

    public static List<SlotEntry> Generate(SlotLayoutOptions opt)
    {
        // 工位号 = 起始工位 + 序号，必须整体落在 1..SlotMax 内：
        // 起始工位 129 时最多只能生成 128 个工位（129-256）。
        var firstSlotNo = Math.Clamp(opt.StartIndex, 1, SlotMax);
        var count = Math.Clamp(opt.SlotCount, 1, SlotMax - firstSlotNo + 1);
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
            var slotNo = firstSlotNo + i;
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
