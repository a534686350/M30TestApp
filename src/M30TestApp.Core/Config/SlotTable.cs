using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace M30TestApp.Core.Config;

public sealed record SlotEntry(
    string Slot,
    string SerialNo,
    string Valve,
    string Board,
    string BoardSlotNo,
    string Layer,
    string Fixture,
    string FixtureSlotNo,
    string PressureController,
    string Dmm,
    string Channel,
    string ValveAddr);

/// <summary>
/// Reads `工位对应表.csv`. The file is a UTF-8 CSV with header in Chinese:
///     工位,序列号,阀位,板卡位,板卡工位号,层数,夹具位,夹具工位号,压力控制器,数字万用表,通道,阀门
/// </summary>
public sealed class SlotTable
{
    public IReadOnlyList<SlotEntry> Entries { get; }

    public SlotTable(IReadOnlyList<SlotEntry> entries) => Entries = entries;

    public static SlotTable Load(string path)
    {
        if (!File.Exists(path)) return new SlotTable(Array.Empty<SlotEntry>());
        var list = new List<SlotEntry>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = line.Split(',');
            string F(int i) => i < c.Length ? c[i].Trim() : "";
            list.Add(new SlotEntry(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11)));
        }
        return new SlotTable(list);
    }
}
