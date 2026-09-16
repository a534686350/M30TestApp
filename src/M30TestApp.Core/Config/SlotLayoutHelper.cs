using System;
using System.Collections.Generic;
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
        var count = Math.Clamp(opt.SlotCount, 1, SlotMax);
        var list = new List<SlotEntry>(count);

        int board = opt.StartBoard;
        int boardSlot = opt.StartBoardSlot;
        int valve = opt.StartValve;
        int fixtureSlot = 1;
        int fixture = 1;
        int layer = 1;
        int channel = opt.StartChannel;
        int serial = opt.StartSerial;

        for (int i = 0; i < count; i++)
        {
            string slotName = $"Slot{i + 1}";
            string serialNo = opt.AutoNumber
                ? $"{opt.BatchNo}#{serial}"
                : $"{opt.BatchNo}#{i + opt.StartIndex}";

            list.Add(new SlotEntry(
                Slot: slotName,
                SerialNo: serialNo,
                Valve: valve.ToString(),
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
            if ((i + 1) % 32 == 0) valve++;
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
