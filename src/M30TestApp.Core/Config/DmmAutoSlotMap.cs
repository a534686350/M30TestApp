using System;
using System.Collections.Generic;

namespace M30TestApp.Core.Config;

/// <summary>
/// 万用表自动测试的工位 → DAQ973A 通道映射。
///
/// 一台 973 管 <see cref="DmmAutoChannelConfig.SlotsPerUnit"/> 个工位（默认 16），
/// 三组通道分工由 <see cref="DmmAutoChannelConfig"/> 指定（现场可改）：
///   电压组 / 电阻组 / 工位电源开关组，外加总电源通道。
/// 默认：电压 101-116、电阻 201-216、工位电源 301-316、总电源 319/320。
///
/// 最多 <see cref="MaxUnits"/> 台 973，按工位顺序分配：
///   工位 1-16 → 973-1，17-32 → 973-2，33-48 → 973-3。
///
/// 注意：这里控制的「电源开关」与电路板测试的「气路阀门」（<c>ValveRelay</c>）是两套
/// 完全独立的硬件。通道号可能撞车，但语义无关，代码路径不得互相复用。
///
/// 所有通道计算方法都接受可选的 <see cref="DmmAutoChannelConfig"/>；传 null 时走默认值，
/// 老调用点行为不变。
/// </summary>
public static class DmmAutoSlotMap
{
    /// <summary>默认单台 973 负责的工位数（未配置时）。</summary>
    public const int DefaultSlotsPerUnit = 16;

    /// <summary>最多支持的 973 台数。</summary>
    public const int MaxUnits = 3;

    /// <summary>默认工位总数上限（= 3 台 × 16）。实际上限见 <see cref="MaxSlotCount"/>。</summary>
    public const int MaxSlot = DefaultSlotsPerUnit * MaxUnits;

    /// <summary>
    /// 当前生效的通道配置（由 <c>TestSession</c> 加载设置时赋值，配置页保存后刷新）。
    /// 单机单会话应用，用静态当前值可让所有老调用点零改动地吃到新配置。
    /// </summary>
    public static DmmAutoChannelConfig Current
    {
        get => _current;
        set => _current = value ?? DmmAutoChannelConfig.Default;
    }

    /// <summary>
    /// 配置页（UI 线程）写、采集任务（后台线程）读，普通静态字段不保证跨线程可见，故用 volatile。
    /// </summary>
    private static volatile DmmAutoChannelConfig _current = DmmAutoChannelConfig.Default;

    /// <summary>当前配置下单台 973 负责的工位数。</summary>
    public static int SlotsPerUnit => Math.Max(1, _current.SlotsPerUnit);

    /// <summary>当前配置下的工位总数上限。</summary>
    public static int MaxSlotCount => SlotsPerUnit * MaxUnits;

    private static DmmAutoChannelConfig Cfg(DmmAutoChannelConfig? cfg) =>
        cfg ?? _current;

    private static int PerUnit(DmmAutoChannelConfig? cfg) =>
        Math.Max(1, Cfg(cfg).SlotsPerUnit);

    /// <summary>工位号 → 973 台号（0-based，0..MaxUnits-1）。</summary>
    public static int UnitIndexForSlot(int slotNo, DmmAutoChannelConfig? cfg = null) =>
        Math.Clamp((Math.Max(1, slotNo) - 1) / PerUnit(cfg), 0, MaxUnits - 1);

    /// <summary>工位号在所属 973 内的序号（0-based）。</summary>
    public static int LocalIndexForSlot(int slotNo, DmmAutoChannelConfig? cfg = null) =>
        (Math.Max(1, slotNo) - 1) % PerUnit(cfg);

    /// <summary>电压采集通道。</summary>
    public static int VoltageChannelForSlot(int slotNo, DmmAutoChannelConfig? cfg = null) =>
        Cfg(cfg).VoltageStart + LocalIndexForSlot(slotNo, cfg);

    /// <summary>电阻采集通道。</summary>
    public static int ResistanceChannelForSlot(int slotNo, DmmAutoChannelConfig? cfg = null) =>
        Cfg(cfg).ResistanceStart + LocalIndexForSlot(slotNo, cfg);

    /// <summary>工位电源开关通道。</summary>
    public static int PowerSwitchChannelForSlot(int slotNo, DmmAutoChannelConfig? cfg = null) =>
        Cfg(cfg).PowerSwitchStart + LocalIndexForSlot(slotNo, cfg);

    /// <summary>总电源通道（上电时同时闭合）。</summary>
    public static IReadOnlyList<string> MasterPowerChannels(DmmAutoChannelConfig? cfg = null) =>
        Cfg(cfg).MasterChannelList;

    /// <summary>某台 973 负责的工位号区间文本，如 "1-16"。</summary>
    public static string UnitSlotRangeText(int unitIndex, DmmAutoChannelConfig? cfg = null)
    {
        var per = PerUnit(cfg);
        var first = unitIndex * per + 1;
        return $"{first}-{first + per - 1}";
    }

    /// <summary>某台 973 的通道映射提示文本（界面展示用）。</summary>
    public static string UnitChannelHint(int unitIndex, DmmAutoChannelConfig? cfg = null)
    {
        var c = Cfg(cfg);
        var per = PerUnit(cfg);
        var range = UnitSlotRangeText(unitIndex, cfg);
        var vEnd = c.VoltageStart + per - 1;
        var rEnd = c.ResistanceStart + per - 1;
        var sEnd = c.PowerSwitchStart + per - 1;
        return $"973-{unitIndex + 1}（工位 {range}）：电压 {c.VoltageStart}-{vEnd} / " +
               $"电阻 {c.ResistanceStart}-{rEnd} / 电源 {c.PowerSwitchStart}-{sEnd} + 总电源 {c.MasterChannels}";
    }

    /// <summary>按工位数推导需要几台 973（至少 1 台，最多 <see cref="MaxUnits"/>）。</summary>
    public static int UnitsForSlotCount(int slotCount, DmmAutoChannelConfig? cfg = null) =>
        Math.Clamp((Math.Max(1, slotCount) + PerUnit(cfg) - 1) / PerUnit(cfg), 1, MaxUnits);
}
