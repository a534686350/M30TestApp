using System;
using System.Globalization;

namespace M30TestApp.Core.Config;

/// <summary>
/// Modbus 采集板寻址：addr1=板卡位(Board)，addr2=板卡工位号(BoardSlotNo)。
/// 与手动页「采集卡地址 / 通道地址」一致。
/// </summary>
public static class SlotDacAddress
{
    public static (string Card, string Channel) Get(SlotEntry slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.Board) && !string.IsNullOrWhiteSpace(slot.BoardSlotNo))
            return (slot.Board.Trim(), slot.BoardSlotNo.Trim());

        var n = ParseSlotIndex(slot.Slot);
        return (
            ((n - 1) / 16 + 1).ToString(CultureInfo.InvariantCulture),
            ((n - 1) % 16 + 1).ToString(CultureInfo.InvariantCulture));
    }

    public static int ParseSlotIndex(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName)) return 1;
        if (slotName.StartsWith("Slot", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(slotName.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Math.Clamp(n, 1, 256);
        if (int.TryParse(slotName, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return Math.Clamp(n, 1, 256);
        return 1;
    }
}
