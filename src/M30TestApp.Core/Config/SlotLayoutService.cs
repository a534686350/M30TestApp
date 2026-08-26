using System;

namespace M30TestApp.Core.Config;

/// <summary>
/// 工位布局参数快照与共享逻辑。配置中心（ConfigViewModel）与执行设置（RunSetupViewModel）
/// 的工位页各自持有可编辑字段，但板卡映射公式、点位批量生成入参、Setting.ini 读写
/// 必须共用同一份实现，避免成对维护漂移。用法：
/// <code>
/// var snap = CurrentSnapshot.PatchedFromIni(ini, slotMax);   // 读：缺键保留现值
/// (a, b, ...) = snap;                                        // 解构回填 VM 字段
/// CurrentSnapshot.SaveToIni(ini);                            // 写：12 个共享键
/// </code>
/// </summary>
public sealed record SlotLayoutSnapshot(
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
    bool AutoNumber)
{
    // ── 板卡映射计算 ────────────────────────────────────────────────

    public int BoardCount
    {
        get
        {
            var capacity = Math.Max(1, BoardSlotCapacity);
            var firstSlot = Math.Clamp(StartBoardSlot, 1, capacity);
            return Math.Max(1, (firstSlot - 1 + Math.Max(1, SlotCount) + capacity - 1) / capacity);
        }
    }

    public int LastBoard => StartBoard + BoardCount - 1;

    public int LastBoardSlot
    {
        get
        {
            var capacity = Math.Max(1, BoardSlotCapacity);
            var firstSlot = Math.Clamp(StartBoardSlot, 1, capacity);
            var offset = (firstSlot - 1 + Math.Max(1, SlotCount) - 1) % capacity;
            return offset + 1;
        }
    }

    public string BoardMappingSummary =>
        $"板卡 {StartBoard} / 工位 {StartBoardSlot}  →  板卡 {LastBoard} / 工位 {LastBoardSlot}    共 {BoardCount} 块";

    // ── 与 SlotLayoutHelper / Setting.ini 的衔接 ─────────────────────

    /// <summary>转为批量生成工位所需的选项记录。</summary>
    public SlotLayoutOptions ToOptions() => new(
        SlotCount: SlotCount,
        BatchNo: BatchNo,
        StartIndex: StartIndex,
        StartBoard: StartBoard,
        StartBoardSlot: StartBoardSlot,
        BoardSlotCapacity: BoardSlotCapacity,
        StartValve: StartValve,
        FixtureSlotCapacity: FixtureSlotCapacity,
        FixtureCount: FixtureCount,
        StartChannel: StartChannel,
        StartSerial: StartSerial,
        AutoNumber: AutoNumber);

    /// <summary>用 Setting.ini [Slots] 段的共享键修补当前快照；缺失/非法的键保留原值。</summary>
    public SlotLayoutSnapshot PatchedFromIni(IniFile ini, int slotMax) => this with
    {
        SlotCount = int.TryParse(ini.Get("Slots", "Count", ""), out var count) && count > 0
            ? Math.Clamp(count, 1, slotMax)
            : SlotCount,
        BatchNo = NullIfBlank(ini.Get("Slots", "BatchNo", "")) ?? BatchNo,
        StartIndex = ParseInt(ini.Get("Slots", "StartIndex", ""), StartIndex),
        StartBoard = ParseInt(ini.Get("Slots", "StartBoard", ""), StartBoard),
        StartBoardSlot = ParseInt(ini.Get("Slots", "StartBoardSlot", ""), StartBoardSlot),
        BoardSlotCapacity = ParseInt(ini.Get("Slots", "BoardSlotCapacity", ""), BoardSlotCapacity, min: 1),
        StartValve = ParseInt(ini.Get("Slots", "StartValve", ""), StartValve),
        FixtureSlotCapacity = ParseInt(ini.Get("Slots", "FixtureSlotCapacity", ""), FixtureSlotCapacity, min: 1),
        FixtureCount = ParseInt(ini.Get("Slots", "FixtureCount", ""), FixtureCount, min: 1),
        StartChannel = ParseInt(ini.Get("Slots", "StartChannel", ""), StartChannel),
        StartSerial = ParseInt(ini.Get("Slots", "StartSerial", ""), StartSerial),
        AutoNumber = bool.TryParse(ini.Get("Slots", "AutoNumber", ""), out var auto) ? auto : AutoNumber,
    };

    /// <summary>把 12 个共享布局键写回 Setting.ini [Slots] 段。</summary>
    public void SaveToIni(IniFile ini)
    {
        ini.Set("Slots", "Count", SlotCount.ToString());
        ini.Set("Slots", "BatchNo", BatchNo);
        ini.Set("Slots", "StartIndex", StartIndex.ToString());
        ini.Set("Slots", "StartBoard", StartBoard.ToString());
        ini.Set("Slots", "StartBoardSlot", StartBoardSlot.ToString());
        ini.Set("Slots", "BoardSlotCapacity", BoardSlotCapacity.ToString());
        ini.Set("Slots", "StartValve", StartValve.ToString());
        ini.Set("Slots", "FixtureSlotCapacity", FixtureSlotCapacity.ToString());
        ini.Set("Slots", "FixtureCount", FixtureCount.ToString());
        ini.Set("Slots", "StartChannel", StartChannel.ToString());
        ini.Set("Slots", "StartSerial", StartSerial.ToString());
        ini.Set("Slots", "AutoNumber", AutoNumber.ToString());
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ParseInt(string raw, int fallback, int min = int.MinValue) =>
        int.TryParse(raw, out var value) && value >= min ? value : fallback;
}
