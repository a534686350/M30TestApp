namespace M30TestApp.Core.Config;

/// <summary>
/// Long-term stability: 60 slots share DAQ channels with voltage/resistance modes.
/// Slot 1-20 → 101-120, 21-40 → 201-220, 41-60 → 301-320.
/// </summary>
public static class LongTermStabilitySlotMap
{
    public const int MaxSlot = 60;
    public const int SlotsPerBank = 20;

    public static int ChannelForSlot(int slotNo) =>
        (((slotNo - 1) / SlotsPerBank) + 1) * 100 + (((slotNo - 1) % SlotsPerBank) + 1);

    public static int BankForSlot(int slotNo) => ((slotNo - 1) / SlotsPerBank) + 1;

    public static (int StartChannel, int EndChannel) ChannelRange(int startSlot, int endSlot) =>
        (ChannelForSlot(startSlot), ChannelForSlot(endSlot));

    public static string FormatRangeHint(int startSlot, int endSlot)
    {
        var (chStart, chEnd) = ChannelRange(startSlot, endSlot);
        return $"工位 {startSlot}-{endSlot} → 通道 {chStart}-{chEnd}";
    }
}
