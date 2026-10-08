using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace M30TestApp.Core.Config;

/// <summary>
/// 万用表自动测试的通道分配 —— 现场可在「配置 → 设备」页单独修改：
/// 哪一组通道采电压、哪一组采电阻、哪一组切换工位电源、总电源用哪几个通道。
///
/// 默认值（对应 DAQ973A 一台 16 工位）：
///   电压 101-116、电阻 201-216、工位电源开关 301-316、总电源 319 / 320。
///
/// 持久化在 Setting.ini 的 <c>[DmmAutoTest]</c> 段。
/// </summary>
public sealed class DmmAutoChannelConfig
{
    public const string SectionName = "DmmAutoTest";

    /// <summary>
    /// 默认配置（ini 里没有对应项时的回退值）。
    /// 每次返回**新实例** —— 别让调用方拿到共享单例再改字段，那会污染全进程默认值。
    /// </summary>
    public static DmmAutoChannelConfig Default => new();

    /// <summary>单台 973 负责的工位数。</summary>
    public int SlotsPerUnit { get; set; } = 16;

    /// <summary>电压采集通道起始号（默认 101）。</summary>
    public int VoltageStart { get; set; } = 101;

    /// <summary>电阻采集通道起始号（默认 201）。</summary>
    public int ResistanceStart { get; set; } = 201;

    /// <summary>工位电源开关通道起始号（默认 301）。</summary>
    public int PowerSwitchStart { get; set; } = 301;

    private string _masterChannels = "319,320";
    private IReadOnlyList<string>? _masterChannelList;

    /// <summary>总电源通道（逗号分隔，上电时同时闭合；默认 319,320）。</summary>
    public string MasterChannels
    {
        get => _masterChannels;
        set
        {
            if (value == _masterChannels) return;
            _masterChannels = value ?? "";
            _masterChannelList = null;   // 缓存失效，等下次访问时重拆
        }
    }

    /// <summary>
    /// 总电源通道列表（已拆分、去空、去重）。
    /// 结果缓存到本字段被改为止 —— 上电/断电每个温度点都要对每台 973 取一次，
    /// 每次重新 Split + LINQ 纯属浪费。
    /// </summary>
    public IReadOnlyList<string> MasterChannelList =>
        _masterChannelList ??= (_masterChannels ?? "")
            .Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>界面展示用摘要（全项目唯一真源，DeviceConfigSet / 运行设置都走它）。</summary>
    public string Summary =>
        $"电压 {VoltageStart}-{VoltageStart + SlotsPerUnit - 1} / " +
        $"电阻 {ResistanceStart}-{ResistanceStart + SlotsPerUnit - 1} / " +
        $"工位电源 {PowerSwitchStart}-{PowerSwitchStart + SlotsPerUnit - 1} / " +
        $"总电源 {MasterChannels}";

    /// <summary>值拷贝（字段全是值类型 + 一个字符串，可放心独立修改）。</summary>
    public DmmAutoChannelConfig Copy() => new()
    {
        SlotsPerUnit = SlotsPerUnit,
        VoltageStart = VoltageStart,
        ResistanceStart = ResistanceStart,
        PowerSwitchStart = PowerSwitchStart,
        MasterChannels = MasterChannels,
    };

    /// <summary>
    /// 写回 ini 并立即刷新全局生效值（<c>DmmAutoSlotMap.Current</c>）。
    /// 配置页 / 设置页的「保存」按钮都走这里，保证改完通道立刻生效、不必重启。
    /// </summary>
    public void SaveTo(IniFile ini)
    {
        Save(ini);
        DmmAutoSlotMap.Current = Copy();
    }

    public static DmmAutoChannelConfig Load(IniFile ini)
    {
        var d = Default;
        return new DmmAutoChannelConfig
        {
            SlotsPerUnit = ReadInt(ini, "SlotsPerUnit", d.SlotsPerUnit, 1, 64),
            VoltageStart = ReadInt(ini, "VoltageStart", d.VoltageStart, 1, 999),
            ResistanceStart = ReadInt(ini, "ResistanceStart", d.ResistanceStart, 1, 999),
            PowerSwitchStart = ReadInt(ini, "PowerSwitchStart", d.PowerSwitchStart, 1, 999),
            MasterChannels = ini.Get(SectionName, "MasterChannels", d.MasterChannels),
        };
    }

    public void Save(IniFile ini)
    {
        ini.Set(SectionName, "SlotsPerUnit", SlotsPerUnit.ToString(CultureInfo.InvariantCulture));
        ini.Set(SectionName, "VoltageStart", VoltageStart.ToString(CultureInfo.InvariantCulture));
        ini.Set(SectionName, "ResistanceStart", ResistanceStart.ToString(CultureInfo.InvariantCulture));
        ini.Set(SectionName, "PowerSwitchStart", PowerSwitchStart.ToString(CultureInfo.InvariantCulture));
        ini.Set(SectionName, "MasterChannels", MasterChannels ?? "");
    }

    private static int ReadInt(IniFile ini, string key, int fallback, int min, int max) =>
        int.TryParse(ini.Get(SectionName, key, fallback.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : fallback;
}
