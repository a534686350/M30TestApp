using System;
using System.Collections.Generic;
using System.Linq;
using M30TestApp.Core.Common;

namespace M30TestApp.Core.Config;

/// <summary>
/// DAQ973A 两套用途的命令校验 —— 防止「电路板采集」和「万用表采集」串用 973 指令。
///
/// 同一台 973 在两种模式下干的活完全不同：
///   · 电路板采集：把它当**继电器箱**，只用 Open / Close（ROUT:OPEN / ROUT:CLOS），
///     通道来自 [ValveSettings]（总阀 101、阀门 102-109）。
///   · 万用表采集：把它当**采集表**，用 SetVol / SetRes / ReadValue（CONF:VOLT / CONF:RES / READ?），
///     通道来自 [DmmAutoTest]（电压 101-118 / 电阻 201-218 / 电源 301-318 / 总电源 319,320）。
///
/// 两套命令走同一个 <c>Command.ini</c> 型号节，缺键会静默回退到代码里的 fallback —— 现场最难查。
/// 这里在启动时把缺失项和通道冲突一次性写进日志，不阻断运行（老现场可能故意用简化配置）。
/// </summary>
public static class DmmCommandGuard
{
    /// <summary>电路板模式（继电器）必需的命令键。</summary>
    private static readonly string[] RelayKeys = { "Open", "Close" };

    /// <summary>万用表模式（采集）必需的命令键。</summary>
    private static readonly string[] MeasureKeys = { "SetVol", "SetRes", "ReadValue" };

    /// <summary>
    /// 校验所有已配置的 973 型号节。返回告警行（空 = 全部正常）。
    /// </summary>
    public static IReadOnlyList<string> Validate(CommandDictionary commands, StationProfile station, IniFile settings)
    {
        var warnings = new List<string>();
        var dmmModels = station.Units(DeviceKind.Dmm)
            .Select(u => u.Model)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var model in dmmModels)
        {
            var missingRelay = RelayKeys.Where(k => string.IsNullOrWhiteSpace(commands.Raw(model, k))).ToArray();
            var missingMeasure = MeasureKeys.Where(k => string.IsNullOrWhiteSpace(commands.Raw(model, k))).ToArray();

            if (missingRelay.Length > 0)
                warnings.Add($"[{model}] 缺电路板采集(继电器)指令 {string.Join("/", missingRelay)} —— 该模式阀门控制会走代码内置 ROUT 命令");
            if (missingMeasure.Length > 0)
                warnings.Add($"[{model}] 缺万用表采集指令 {string.Join("/", missingMeasure)} —— 该模式电压/电阻读数会走代码内置 CONF/READ 命令");
        }

        // 通道撞号检查：电路板阀门通道 vs 万用表采集/电源通道。
        // 撞号本身不一定错（physically 可能共用），但两种模式同时跑必然互相干扰，必须提示。
        var valveChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i <= 8; i++)
        {
            var ch = ValveRelay.ReadAddress(settings, ValveRelay.KeyForIndex(i), (101 + i).ToString());
            if (!string.IsNullOrWhiteSpace(ch) && ch != "-") valveChannels.Add(ch.Trim());
        }

        var cfg = DmmAutoSlotMap.Current;
        var dmmChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var slot = 1; slot <= DmmAutoSlotMap.SlotsPerUnit; slot++)
        {
            dmmChannels.Add((cfg.VoltageStart + slot - 1).ToString());
            dmmChannels.Add((cfg.ResistanceStart + slot - 1).ToString());
            dmmChannels.Add((cfg.PowerSwitchStart + slot - 1).ToString());
        }
        foreach (var ch in cfg.MasterChannelList) dmmChannels.Add(ch);

        var overlap = valveChannels.Intersect(dmmChannels).ToArray();
        if (overlap.Length > 0)
            warnings.Add($"通道撞号：电路板阀门与万用表采集共用通道 {string.Join(",", overlap)} —— 两种模式不要同时运行");

        return warnings;
    }

    /// <summary>把校验结果写进日志（每条一行 Warn）。</summary>
    public static void LogWarnings(CommandDictionary commands, StationProfile station, IniFile settings)
    {
        foreach (var warning in Validate(commands, station, settings))
            AppLog.Warn("DmmCommand", warning);
    }
}
