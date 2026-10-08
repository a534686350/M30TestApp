using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Mvvm;

/// <summary>
/// 设备配置编辑器。配置页「设备」页与偏好设置「设备」页共用同一份加载 / 保存逻辑，
/// 避免两处各写一套导致行为漂移。
///
/// 每行 = 一类设备的一个实例。DAQ973A 固定铺满 <see cref="DmmAutoSlotMap.MaxUnits"/> 行，
/// 由 <see cref="DmmUnitCount"/> 决定实际写回几台 —— 万用表自动测试 48 工位 = 3 台 × 16。
/// </summary>
public sealed class DeviceConfigSet : ViewModelBase
{
    /// <summary>设备页展示的类别与顺序（现场实际用到的六类）。</summary>
    private static readonly DeviceKind[] ConfigurableKinds =
    {
        DeviceKind.Pressure, DeviceKind.Oven, DeviceKind.Dmm,
        DeviceKind.Dac, DeviceKind.Power, DeviceKind.Board,
    };

    /// <summary>GPIB 设备的总线候选（现场常见 0/1，给到 3 够用）。</summary>
    private static readonly string[] GpibBuses = { "GPIB0", "GPIB1", "GPIB2", "GPIB3" };

    /// <summary>串口设备的串口候选。</summary>
    private static readonly string[] SerialPorts = { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8" };

    private readonly List<DeviceConfigVm> _rows = new();

    /// <summary>当前显示的设备行（DAQ973A 只显示到 <see cref="DmmUnitCount"/> 台）。</summary>
    public ObservableCollection<DeviceConfigVm> Rows { get; } = new();

    private int _dmmUnitCount = 1;

    /// <summary>本机实际使用的 DAQ973A 台数（1-3）。</summary>
    public int DmmUnitCount
    {
        get => _dmmUnitCount;
        set
        {
            var next = Math.Clamp(value, 1, DmmAutoSlotMap.MaxUnits);
            if (!SetField(ref _dmmUnitCount, next)) return;
            OnPropertyChanged(nameof(DmmUnitHint));
            Rebuild();
        }
    }

    public string DmmUnitHint =>
        $"共 {_dmmUnitCount} 台 → 最多 {_dmmUnitCount * DmmAutoSlotMap.SlotsPerUnit} 个工位（每台管 {DmmAutoSlotMap.SlotsPerUnit} 工位）";

    // ─── 万用表通道分配（现场可单独改：哪组采电压 / 哪组采电阻 / 哪组切电源） ───

    // 字段默认值一律取 Core 的 DmmAutoChannelConfig.Default —— 不在这里再抄一份 101/201/301/319,320，
    // 否则改了 Core 默认值、界面还停在旧数字上，现场会照着过期提示接线。
    private int _dmmSlotsPerUnit = DmmAutoChannelConfig.Default.SlotsPerUnit;
    /// <summary>每台 973 负责的工位数。</summary>
    public int DmmSlotsPerUnit
    {
        get => _dmmSlotsPerUnit;
        set { if (SetField(ref _dmmSlotsPerUnit, Math.Clamp(value, 1, 64))) OnPropertyChanged(nameof(DmmChannelHint)); }
    }

    private int _voltageStart = DmmAutoChannelConfig.Default.VoltageStart;
    /// <summary>电压采集通道起始号。</summary>
    public int VoltageStart
    {
        get => _voltageStart;
        set { if (SetField(ref _voltageStart, Math.Clamp(value, 1, 999))) OnPropertyChanged(nameof(DmmChannelHint)); }
    }

    private int _resistanceStart = DmmAutoChannelConfig.Default.ResistanceStart;
    /// <summary>电阻采集通道起始号。</summary>
    public int ResistanceStart
    {
        get => _resistanceStart;
        set { if (SetField(ref _resistanceStart, Math.Clamp(value, 1, 999))) OnPropertyChanged(nameof(DmmChannelHint)); }
    }

    private int _powerSwitchStart = DmmAutoChannelConfig.Default.PowerSwitchStart;
    /// <summary>工位电源开关通道起始号。</summary>
    public int PowerSwitchStart
    {
        get => _powerSwitchStart;
        set { if (SetField(ref _powerSwitchStart, Math.Clamp(value, 1, 999))) OnPropertyChanged(nameof(DmmChannelHint)); }
    }

    private string _masterChannels = DmmAutoChannelConfig.Default.MasterChannels;
    /// <summary>总电源通道（逗号分隔，上电时同时闭合）。</summary>
    public string MasterChannels
    {
        get => _masterChannels;
        set { if (SetField(ref _masterChannels, value ?? "")) OnPropertyChanged(nameof(DmmChannelHint)); }
    }

    /// <summary>
    /// 当前编辑中的通道配置（拿它复用 <see cref="DmmAutoChannelConfig.Summary"/> 这唯一一份摘要文案）。
    /// </summary>
    private DmmAutoChannelConfig Editing => new()
    {
        SlotsPerUnit = Math.Max(1, _dmmSlotsPerUnit),
        VoltageStart = _voltageStart,
        ResistanceStart = _resistanceStart,
        PowerSwitchStart = _powerSwitchStart,
        MasterChannels = _masterChannels,
    };

    /// <summary>按当前输入实时预览通道区间，改完一眼能看到实际会用到哪些通道。</summary>
    public string DmmChannelHint => Editing.Summary;

    /// <summary>从 Setting.ini 读入全部设备配置行与万用表通道分配。</summary>
    public void Load(IniFile ini)
    {
        _rows.Clear();

        var channels = DmmAutoChannelConfig.Load(ini);
        _dmmSlotsPerUnit = channels.SlotsPerUnit;
        _voltageStart = channels.VoltageStart;
        _resistanceStart = channels.ResistanceStart;
        _powerSwitchStart = channels.PowerSwitchStart;
        _masterChannels = channels.MasterChannels;
        OnPropertyChanged(nameof(DmmSlotsPerUnit));
        OnPropertyChanged(nameof(VoltageStart));
        OnPropertyChanged(nameof(ResistanceStart));
        OnPropertyChanged(nameof(PowerSwitchStart));
        OnPropertyChanged(nameof(MasterChannels));
        OnPropertyChanged(nameof(DmmChannelHint));

        var profile = StationProfile.Load(ini);
        var dmmEnabled = profile.Get(DeviceKind.Dmm)?.Backend == DeviceBackend.Hw;

        foreach (var kind in ConfigurableKinds)
        {
            var units = profile.Units(kind);
            var rowCount = kind == DeviceKind.Dmm ? DmmAutoSlotMap.MaxUnits : units.Count;

            for (var i = 0; i < rowCount; i++)
            {
                var unit = i < units.Count ? units[i] : null;
                _rows.Add(new DeviceConfigVm
                {
                    Kind = kind,
                    UnitIndex = i,
                    IsKindFirst = i == 0,
                    Title = kind == DeviceKind.Dmm ? $"DAQ973A #{i + 1}" : KindTitle(kind),
                    Hint = kind == DeviceKind.Dmm
                        ? $"工位 {DmmAutoSlotMap.UnitSlotRangeText(i)}"
                        : KindHint(kind),
                    IsSerial = IsSerialKind(kind),
                    BusOptions = IsSerialKind(kind) ? SerialPorts : GpibBuses,
                    // 后端开关按类别共享：ini 里还没有的预留行跟随本类第一行。
                    Enabled = unit is null ? dmmEnabled : unit.Backend == DeviceBackend.Hw,
                    Model = unit?.Model ?? (kind == DeviceKind.Dmm ? "DAQ973A" : ""),
                    Address = unit?.Address ?? "",
                    Baud = unit?.Baud ?? 9600,
                    Parity = unit?.Parity ?? "N",
                    DataBits = unit?.DataBits ?? 8,
                    StopBits = unit?.StopBits ?? "1",
                });
            }
        }

        _dmmUnitCount = Math.Clamp(
            Math.Max(1, profile.Units(DeviceKind.Dmm).Count), 1, DmmAutoSlotMap.MaxUnits);
        OnPropertyChanged(nameof(DmmUnitCount));
        OnPropertyChanged(nameof(DmmUnitHint));
        Rebuild();
    }

    /// <summary>把编辑写回 ini（不落盘、不重建设备，由调用方决定时机）。</summary>
    public void Save(IniFile ini)
    {
        // 先校验：地址没配全就写进 ini，现场表现是「设备连不上」却查不出原因。
        // 地址没配全的行（总线空 / GPIB 地址号空）跳过不写，保留 ini 里的旧值，只记日志。
        // 曾经想抛异常拦一下，但配置页的「保存全部」也走这里 ——
        // 一个设备没填地址就把方案 / 工位 / 指令的一键保存全废掉，比漏写一个地址更糟。
        var incomplete = Rows.Where(r => r.IsActive && r.Enabled && !r.IsAddressComplete).ToList();
        foreach (var bad in incomplete)
            AppLog.Warn("Config",
                $"设备地址未配完整，该设备本次不写入（{bad.Title}，当前「{bad.AddressPreview}」），ini 里保留原值");

        // 落 ini 后立刻刷新全局生效值，后续生成工位表即按新通道走。
        Editing.SaveTo(ini);

        foreach (var group in _rows.GroupBy(r => r.Kind))
        {
            // 后端开关（SIM/HW）按类别共享，取该类第一行。
            ini.Set("DefaultLoadClass", group.Key.ToString(), group.First().Enabled ? "HW" : "SIM");

            foreach (var row in group)
            {
                var section = StationProfile.SectionName(row.Kind, row.UnitIndex);

                // 超出当前台数的 973：整段删除，避免留下指向不存在设备的地址。
                if (row.IsDmmUnit && row.UnitIndex >= _dmmUnitCount)
                {
                    ini.RemoveSection(section);
                    continue;
                }

                // 地址没配全（总线空 / GPIB 地址号空）：本次不写这一段，ini 里保留原值。
                if (!row.IsAddressComplete)
                    continue;

                ini.Set(section, "Model", (row.Model ?? "").Trim());
                ini.Set(section, "Address", (row.Address ?? "").Trim());
                if (row.IsSerial)
                {
                    ini.Set(section, "Baud", row.Baud.ToString(CultureInfo.InvariantCulture));
                    ini.Set(section, "Parity", row.Parity);
                    ini.Set(section, "DataBits", row.DataBits.ToString(CultureInfo.InvariantCulture));
                    ini.Set(section, "StopBits", row.StopBits);
                }
            }
        }
    }

    /// <summary>
    /// 保存设备配置并**立即生效**：落盘 → 刷新会话的 Setting.ini 引用 → 同步 Station 两套字典 → 重建设备。
    ///
    /// 配置页与偏好设置页共用这一个入口。以前偏好设置页只 Save + RebuildDevices，
    /// 没同步 <c>Station.Devices</c> / <c>AllUnits</c>，于是「在偏好设置里改了设备地址」
    /// 本次会话不生效，必须再去配置页点一次保存 —— 而同一次保存里通道配置却立刻生效，行为不一致。
    /// </summary>
    public string SaveAndApply(TestSession session, IniFile ini, bool debugMode)
    {
        Save(ini);
        ini.Save(AppPaths.SettingIni);
        session.Context.Settings = ini;
        session.ReloadStation(ini);
        return $"设备配置已保存并重建设备（DAQ973A × {DmmUnitCount}）。";
    }

    /// <summary>
    /// 重建可见行：全部行都显示（DAQ973A 的 #2 / #3 也常显，否则用户根本不知道能配几台），
    /// 超出当前台数的行标记 <see cref="DeviceConfigVm.IsActive"/>=false 由界面灰显。
    /// </summary>
    private void Rebuild()
    {
        Rows.Clear();
        foreach (var row in _rows)
        {
            row.IsActive = !row.IsDmmUnit || row.UnitIndex < _dmmUnitCount;
            Rows.Add(row);
        }
    }

    private static string KindTitle(DeviceKind kind) => kind switch
    {
        DeviceKind.Pressure => "压力控制器",
        DeviceKind.Oven => "烘箱",
        DeviceKind.Dmm => "DAQ973A",
        DeviceKind.Dac => "采集板",
        DeviceKind.Power => "电源",
        DeviceKind.Board => "通道板",
        _ => kind.ToString(),
    };

    private static string KindHint(DeviceKind kind) => kind switch
    {
        DeviceKind.Pressure => "控压 / 保压 / 探漏",
        DeviceKind.Oven => "温度设定与实测",
        DeviceKind.Dac => "电路板采集 UT / USC / ISC / USG",
        DeviceKind.Power => "传感器供电",
        DeviceKind.Board => "通道切换",
        _ => "",
    };

    private static bool IsSerialKind(DeviceKind kind) =>
        kind is DeviceKind.Oven or DeviceKind.Dac or DeviceKind.Board;
}
