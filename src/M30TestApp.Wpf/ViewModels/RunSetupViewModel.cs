using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// View model for the "选择运行方案 / 工位" pre-run dialog.
/// Lets the operator pick which TestPlan to use for this run and (re)generate
/// the per-run slot table from a compact set of layout parameters.
/// </summary>
public sealed class RunSetupViewModel : ViewModelBase
{
    private readonly TestSession _session;
    private Dictionary<string, string> _configSerials = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string X, string Y)> _configPositions = new(StringComparer.OrdinalIgnoreCase);
    private TestCheckpoint? _checkpoint;
    private readonly bool _isLongTermStabilityMode;

    // ─── Plan selection ────────────────────────────────────────────────────
    public ObservableCollection<string> PlanFolders { get; } = new();
    public ObservableCollection<string> SensorModels { get; } = new();

    private TestPlan? _selectedPlan;
    public TestPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (SetField(ref _selectedPlan, value))
            {
                OnPropertyChanged(nameof(PlanSensorType));
                OnPropertyChanged(nameof(PlanPressureUnit));
                OnPropertyChanged(nameof(PlanPrecision));
                OnPropertyChanged(nameof(PlanPressureCount));
                OnPropertyChanged(nameof(PlanTempCount));
                OnPropertyChanged(nameof(PlanDefaultPressureType));
                OnPropertyChanged(nameof(PlanTaskPreview));
                OnPropertyChanged(nameof(ReportModelHint));
                UpdateCheckpointState();
            }
        }
    }

    public string PlanSensorType    => _selectedPlan?.SensorType ?? "—";
    public string PlanPressureUnit  => _selectedPlan?.PressureUnit ?? "—";
    public string PlanPrecision     => _selectedPlan is null ? "—" : $"{_selectedPlan.Precision}";
    public int    PlanPressureCount => _selectedPlan?.PressurePoints.Count ?? 0;
    public int    PlanTempCount     => _selectedPlan?.TempPoints.Count ?? 0;
    public string PlanDefaultPressureType => _selectedPlan is null ? "—" : _selectedPlan.DefaultPressureType switch
    {
        Core.Config.PressureType.Absolute     => "绝压",
        Core.Config.PressureType.Differential => "差压",
        _                                     => "表压",
    };

    private string _reportDevice = "";

    /// <summary>
    /// 报表「测试设备」栏（性能测试报表的 AO 列）。开测前在本窗口选择或直接录入，
    /// 留空回落到 [Device.Pressure] Model；录入后写入 Setting.ini 的 [Report] DeviceName。
    /// </summary>
    public string ReportDevice
    {
        get => _reportDevice;
        set => SetField(ref _reportDevice, value);
    }

    /// <summary>
    /// 测试设备候选：内置默认 + 缓存的往期报表 AO 列扫描结果。
    /// 候选里没有的型号可直接在下拉框里输入，本轮测试即时生效。
    /// </summary>
    public ObservableCollection<string> ReportDeviceOptions { get; } = new();

    // ─── 报表模板选择 ──────────────────────────────────────────────────────

    public const string AutoTemplateLabel = "自动测试模板（全性能）";
    public const string WaferTemplateLabel = "晶圆测试模板（现场样例）";

    /// <summary>生成模板候选。</summary>
    public ObservableCollection<string> ReportTemplateOptions { get; } = new()
    {
        AutoTemplateLabel,
        WaferTemplateLabel,
    };

    private string _selectedReportTemplate = AutoTemplateLabel;

    /// <summary>
    /// 本次测试的报表生成模板：
    ///   自动测试模板 = 沿用全性能.xlsx，桌面仍写旧版兼容 CSV（行为不变）；
    ///   晶圆测试模板 = 用「生成的数据格式」下的现场样例生成，桌面另存一份同名 xlsx。
    /// </summary>
    public string SelectedReportTemplate
    {
        get => _selectedReportTemplate;
        set
        {
            if (!SetField(ref _selectedReportTemplate, value)) return;
            OnPropertyChanged(nameof(SelectedTemplateMode));
            OnPropertyChanged(nameof(ReportTemplateHint));
        }
    }

    public ReportTemplateMode SelectedTemplateMode =>
        string.Equals(_selectedReportTemplate, WaferTemplateLabel, StringComparison.Ordinal)
            ? ReportTemplateMode.Wafer
            : ReportTemplateMode.Auto;

    /// <summary>提示实际会用到的模板文件，方便现场核对模板是否换成功。</summary>
    public string ReportTemplateHint
    {
        get
        {
            var path = TemplatePerformanceExporter.ResolveTemplatePath(SelectedTemplateMode);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "⚠ 未找到模板文件";
            return "模板：" + Path.GetFileName(path);
        }
    }

    private string _reportWaferNo = "";

    /// <summary>
    /// 报表「晶圆编号」（性能测试.xlsx 的 C 列第一行）。留空 = 自动取序列号去掉末段工位序号。
    /// 录入后写入 Setting.ini 的 [Report] WaferNo。
    /// </summary>
    public string ReportWaferNo
    {
        get => _reportWaferNo;
        set => SetField(ref _reportWaferNo, value);
    }

    private string _reportModel = "";

    /// <summary>
    /// 报表「型号」（C 列第二行 + 导出文件名）。留空 = 自动取方案传感器型号去掉 M30- 前缀。
    /// 录入后写入 Setting.ini 的 [Report] Model。
    /// </summary>
    public string ReportModel
    {
        get => _reportModel;
        set => SetField(ref _reportModel, value);
    }

    /// <summary>留空时实际会写入报表的编号 / 型号，给界面做占位提示。</summary>
    public string ReportWaferNoHint => string.IsNullOrWhiteSpace(_reportWaferNo)
        ? "自动：" + AutoWaferNo()
        : "已指定";

    public string ReportModelHint => string.IsNullOrWhiteSpace(_reportModel)
        ? "自动：" + AutoModel()
        : "已指定";

    private string AutoModel() => TemplatePerformanceExporter.ModelName(_selectedPlan?.SensorType ?? "");

    private string AutoWaferNo()
    {
        var serial = PreviewSlots.FirstOrDefault()?.SerialNo ?? "";
        var idx = serial.LastIndexOf('-');
        return idx > 0 && serial[(idx + 1)..].All(char.IsDigit) ? serial[..idx] : serial;
    }

    public string PlanTaskPreview
    {
        get
        {
            var plan = _selectedPlan;
            if (plan is null) return "";
            if (UseDmmAutoTest)
                return ExpandDmmAutoTestPreview(plan);
            
            var s = plan.TaskScript ?? "";
            if (s.Length == 0) return "";

            if (_isLongTermStabilityMode)
                return ExpandLongTermStabilityPreview(plan);

            var cmds = s.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            if (cmds.Count == 1 && cmds[0].Equals("Run:PerformanceTest", StringComparison.OrdinalIgnoreCase))
                return ExpandPerformancePreview(plan);

            return string.Join(Environment.NewLine, cmds);
        }
    }

    private static string ExpandPerformancePreview(TestPlan plan)
    {
        var lines = new List<string>();
        lines.Add("═══ Run:PerformanceTest 完整流程 ═══");
        lines.Add("");
        lines.Add("【1】探漏");
        var leakPressures = LeakCheckPlanHelper.ResolvePressures(plan);
        var leakPrecision = LeakCheckPlanHelper.ResolvePrecision(plan);
        var leakPrecisionPa = PressureRateToPa(leakPrecision, plan.PressureUnit);
        if (leakPressures.Count > 0)
        {
            lines.Add($"    压力类型 {plan.DefaultPressureType}，压力点 {string.Join(" / ", leakPressures)}{plan.PressureUnit}，阈值 {leakPrecisionPa:G4}Pa/s");
            lines.Add("    逐阀加压 → 检测泄漏率 → 全开整体探漏（每个压力点重复）");
        }
        else
            lines.Add("    逐阀加压 → 检测泄漏率 → 全开整体探漏");
        lines.Add("");

        for (var ti = 0; ti < plan.TempPoints.Count; ti++)
        {
            var tp = plan.TempPoints[ti];
            var soak = tp.SoakMinutes ?? 120;
            lines.Add($"【{ti + 2}】温度点 {tp.Name} = {tp.Celsius} °C");
            lines.Add($"    设置烘箱温度 → 等待到达（最长240min）");
            lines.Add($"    保温 {soak} min（每分钟日志）");
            lines.Add($"    采集 UT（每温度点1次）");

            for (var pi = 0; pi < plan.PressurePoints.Count; pi++)
            {
                var pp = plan.PressurePoints[pi];
                lines.Add($"    ── 压力点 {pp.Name} = {pp.Value} {plan.PressureUnit} [{pp.PressureTypeDisplay}]");
                lines.Add($"       切换压力类型 → {pp.PressureTypeDisplay} → 设置压力 → 稳定 → 保压");
                lines.Add($"       逐工位采集 USC, ISC, USG");
            }
            lines.Add("");
        }

        lines.Add("【结束】保存数据 → 泄压");
        return string.Join(Environment.NewLine, lines);
    }

    private string _selectedPlanFolder = "";
    public string SelectedPlanFolder
    {
        get => _selectedPlanFolder;
        set
        {
            if (!SetField(ref _selectedPlanFolder, value)) return;
            LoadSensorModels();
        }
    }

    private string _selectedSensorModel = "";
    public string SelectedSensorModel
    {
        get => _selectedSensorModel;
        set
        {
            if (!SetField(ref _selectedSensorModel, value)) return;
            LoadSelectedPlan();
        }
    }

    private string ExpandLongTermStabilityPreview(TestPlan plan)
    {
        var isResistance = LongTermMeasureMode == LongTermStabilityMeasureMode.Resistance;
        var dmmLabel = isResistance ? "电阻 Ω" : "电压 mV";
        var dmmCmd = isResistance ? "CONF:RES (@CH) → READ?" : "CONF:VOLT (@CH) → READ?";

        var lines = new List<string>();
        lines.Add("═══ Run:LongTermStabilityTest 长期稳定性测试 ═══");
        lines.Add("");
        lines.Add($"采集项：DAQ973A {dmmLabel}（{dmmCmd}），通道 101-120 / 201-220 / 301-320（60 工位，电压/电阻共用）。");
        lines.Add("工位范围：运行设置中可指定起始/结束工位（如 21-40 → 201-220）。");
        lines.Add("不采集 UT / USC / ISC / USG。");
        lines.Add("表格列：每个温度点 → 全部压力点 DMM 列 + 1 列 Temp（全部压力采集完成后读一次烘箱温度）。");
        lines.Add("");

        var tableCols = LongTermStabilityMatrix.BuildColumns(plan, LongTermMeasureMode);
        if (tableCols.Count > 0)
        {
            lines.Add("数据矩阵列预览：");
            foreach (var col in tableCols)
                lines.Add($"  · {col.FlatHeader}");
            lines.Add("");
        }

        for (var ti = 0; ti < plan.TempPoints.Count; ti++)
        {
            var tp = plan.TempPoints[ti];
            var soak = tp.SoakMinutes ?? 120;
            lines.Add($"【{ti + 1}】温度点 {tp.Name} = {tp.Celsius} °C");
            lines.Add($"    设置烘箱温度 → 等待到达 → 保温 {soak} min");

            foreach (var pp in plan.PressurePoints)
            {
                lines.Add($"    ── 压力点 {pp.Name} = {pp.Value} {plan.PressureUnit} [{pp.PressureTypeDisplay}]");
                lines.Add($"       设置压力 → 稳定 → 保压 → DAQ973A 逐工位采集 {dmmLabel}");
            }
            lines.Add("    全部压力点完成后 → 读取烘箱温度 Temp（仅一次，写入全部工位）");
            lines.Add("");
        }

        lines.Add("【结束】保存数据 → 泄压");
        return string.Join(Environment.NewLine, lines);
    }

    private string ExpandDmmAutoTestPreview(TestPlan plan)
    {
        var lines = new List<string>();
        lines.Add("═══ Run:DmmAutoTest 万用表自动测试 ═══");
        lines.Add("");
        var cfg = ChannelConfig;
        var swEnd = cfg.PowerSwitchStart + DmmAutoSlotMap.SlotsPerUnit - 1;
        lines.Add($"DAQ973A × {DmmAutoTestUnitCount}，工位 {_startIndex}-{EndIndex}（模式上限 {DmmAutoTestSlotLimit}）。");
        lines.Add($"通道分配（以「配置 → 设备」里保存的为准）：{cfg.Summary}。");
        lines.Add("控制对象是传感器电源开关，全程不控制气路阀门。");
        lines.Add("电压按 mV 写入 USG 指标列，电阻写入 DMM_R 并优先用于 TCR。");
        lines.Add("");

        for (var ti = 0; ti < plan.TempPoints.Count; ti++)
        {
            var tp = plan.TempPoints[ti];
            var soak = tp.SoakMinutes ?? 120;
            lines.Add($"【{ti + 1}】温度点 {tp.Name} = {tp.Celsius} °C");
            lines.Add($"    设置烘箱 → 等待到达 → 保温 {soak} min");

            foreach (var pp in plan.PressurePoints)
            {
                lines.Add($"    ── 压力点 {pp.Name} = {pp.Value} {plan.PressureUnit} [{pp.PressureTypeDisplay}]");
                lines.Add("       设压力 → 稳定 → 保压");
                lines.Add($"       上电：{cfg.PowerSwitchStart}-{swEnd} + 总电源 {cfg.MasterChannels} 同时闭合 → 逐工位读电压 USG");
                lines.Add($"       断电：{cfg.PowerSwitchStart}-{swEnd} 与总电源 {cfg.MasterChannels} 全部断开");
            }

            lines.Add("    逐工位读电阻 → DMM_R");
            lines.Add("    读取烘箱温度 → OvenTemp（全部工位）");
            lines.Add("");
        }

        lines.Add("【结束】计算指标 → 保存数据 → 泄压 → 关烘箱");
        return string.Join(Environment.NewLine, lines);
    }

    // ─── Slot layout inputs ────────────────────────────────────────────────
    public const int SlotMax = SlotLayoutHelper.SlotMax;
    public const int LongTermStabilitySlotMax = 60;
    /// <summary>万用表采集模式最多支持的工位数 = 3 台 × 每台工位数（默认 48）。</summary>
    public int DmmAutoTestSlotMax => DmmAutoSlotMap.MaxSlotCount;

    /// <summary>万用表采集模式实际可用的工位上限 = 已配置的 973 台数 × 16。</summary>
    public int DmmAutoTestSlotLimit =>
        Math.Clamp(_session.Dmms.Count, 1, DmmAutoSlotMap.MaxUnits) * DmmAutoSlotMap.SlotsPerUnit;

    /// <summary>本次万用表采集会用到的 973 台数（1-3）。</summary>
    public int DmmAutoTestUnitCount =>
        Math.Clamp((SlotCount + DmmAutoSlotMap.SlotsPerUnit - 1) / DmmAutoSlotMap.SlotsPerUnit,
            1, Math.Clamp(_session.Dmms.Count, 1, DmmAutoSlotMap.MaxUnits));

    public int SlotLimit => _isLongTermStabilityMode
        ? LongTermStabilitySlotMax
        : UseDmmAutoTest ? DmmAutoTestSlotLimit : SlotMax;
    public bool IsLongTermStabilityMode => _isLongTermStabilityMode;
    public bool ShowLongTermSlotLayout => _isLongTermStabilityMode;
    public bool ShowLegacySlotLayoutFields => !_isLongTermStabilityMode;

    /// <summary>万用表模式下「板卡映射 / 夹具阀位」整块无意义，应隐藏。</summary>
    public bool ShowBoardLayoutFields => !_isLongTermStabilityMode && !UseDmmAutoTest;

    /// <summary>
    /// 当前生效的通道配置（来自「配置 → 设备」保存的 <c>[DmmAutoTest]</c> 段）。
    /// 提示文案一律从它生成 —— 以前这里写死 101-116/201-216/301-316/319-320，
    /// 现场改了通道，界面提示还停在旧数字上，照着接线就错。
    /// </summary>
    private static DmmAutoChannelConfig ChannelConfig => DmmAutoSlotMap.Current;

    /// <summary>万用表模式替代「板卡映射」显示的通道摘要。</summary>
    public string DmmChannelSummary =>
        $"{DmmAutoTestUnitCount} 台 DAQ973A × {DmmAutoSlotMap.SlotsPerUnit} 工位\n{ChannelConfig.Summary}";

    public string ChannelHint => _isLongTermStabilityMode
        ? "通道映射：1-20→101-120，21-40→201-220，41-60→301-320（电压/电阻共用）"
        : UseDmmAutoTest
            ? string.Join(Environment.NewLine,
                Enumerable.Range(0, DmmAutoTestUnitCount).Select(i => DmmAutoSlotMap.UnitChannelHint(i)))
            : "";

    public string LongTermSlotRangeHint => _isLongTermStabilityMode
        ? LongTermStabilitySlotMap.FormatRangeHint(_startIndex, EndIndex)
        : "";

    private int _slotCount = 16;
    public int SlotCount
    {
        get => _slotCount;
        set
        {
            var next = Math.Clamp(value, 1, MaxSlotCountFromStart);
            if (!SetField(ref _slotCount, next)) return;
            NotifySlotRangeChanged();
            NotifyBoardLayoutChanged();
            Regenerate();
        }
    }

    private string _batchNo = $"{DateTime.Now:yyMMdd}_01";
    public string BatchNo { get => _batchNo; set => SetLayoutField(ref _batchNo, value); }

    private int _startIndex = 1;
    public int StartIndex
    {
        get => _startIndex;
        set
        {
            // 工位号直接决定生成的 Slot 名与阀门号，必须落在 1..工位上限内。
            var next = Math.Clamp(value, 1, SlotLimit);
            if (!SetField(ref _startIndex, next)) return;
            if (_slotCount > MaxSlotCountFromStart)
            {
                _slotCount = MaxSlotCountFromStart;
                OnPropertyChanged(nameof(SlotCount));
            }
            SyncDerivedLayoutFromStartIndex();
            NotifySlotRangeChanged();
            NotifyBoardLayoutChanged();
            Regenerate();
        }
    }

    /// <summary>
    /// 「起始阀位 / 起始板卡 / 起始板卡工位」不再是独立的手填参数，全部由起始工位推导：
    /// 阀门每 32 工位一个（起始 129 → 5 号阀），板卡每 BoardSlotCapacity 工位一块
    /// （起始 129、每板 16 工位 → 板卡 9 / 工位 1）。
    /// 输入框仍可编辑（历史兼容），但起始工位一变就按工位重算覆盖，
    /// 避免出现「起始工位 129 却从 1 号板卡 / 1 号阀开始数」的错配。
    /// </summary>
    private void SyncDerivedLayoutFromStartIndex()
    {
        var zeroBased = Math.Max(0, _startIndex - 1);

        var valve = SlotLayoutHelper.ValveForSlot(_startIndex);
        if (_startValve != valve)
        {
            _startValve = valve;
            OnPropertyChanged(nameof(StartValve));
        }

        var capacity = Math.Max(1, _boardSlotCapacity);
        var board = zeroBased / capacity + 1;
        var boardSlot = zeroBased % capacity + 1;
        if (_startBoard != board)
        {
            _startBoard = board;
            OnPropertyChanged(nameof(StartBoard));
        }
        if (_startBoardSlot != boardSlot)
        {
            _startBoardSlot = boardSlot;
            OnPropertyChanged(nameof(StartBoardSlot));
        }
    }

    /// <summary>结束工位（含）；与起始工位、工位数联动。</summary>
    public int EndIndex
    {
        get => Math.Min(SlotLimit, Math.Max(_startIndex, _startIndex + _slotCount - 1));
        set
        {
            var end = Math.Clamp(value, _startIndex, SlotLimit);
            var nextCount = end - _startIndex + 1;
            if (nextCount == _slotCount)
            {
                OnPropertyChanged();
                return;
            }
            _slotCount = nextCount;
            OnPropertyChanged(nameof(SlotCount));
            NotifySlotRangeChanged();
            NotifyBoardLayoutChanged();
            Regenerate();
        }
    }

    private void NotifySlotRangeChanged()
    {
        OnPropertyChanged(nameof(EndIndex));
        OnPropertyChanged(nameof(LongTermSlotRangeHint));
        OnPropertyChanged(nameof(ValveRangeHint));
        OnPropertyChanged(nameof(DmmAutoTestUnitCount));
        OnPropertyChanged(nameof(DmmChannelSummary));
        OnPropertyChanged(nameof(ChannelHint));
    }

    /// <summary>从起始工位到工位上限还能生成多少个工位（起始 129 → 最多 128 个）。</summary>
    private int MaxSlotCountFromStart =>
        Math.Max(1, SlotLimit - Math.Clamp(_startIndex, 1, SlotLimit) + 1);

    private int _startBoard = 1;
    public int StartBoard { get => _startBoard; set => SetLayoutField(ref _startBoard, value); }

    private int _startBoardSlot = 1;
    public int StartBoardSlot { get => _startBoardSlot; set => SetLayoutField(ref _startBoardSlot, value); }

    private int _boardSlotCapacity = 16;
    public int BoardSlotCapacity { get => _boardSlotCapacity; set => SetLayoutField(ref _boardSlotCapacity, Math.Max(1, value)); }

    /// <summary>
    /// 起始阀位。只作为「由起始工位推导出来的结果」展示（每 32 工位一个阀），
    /// 真实取值一律由 <see cref="SyncDerivedLayoutFromStartIndex"/> 按工位重算，
    /// 手填值不会影响生成的阀门列与运行时实际打开的阀门。
    /// </summary>
    private int _startValve = 1;
    public int StartValve
    {
        get => _startValve;
        set
        {
            if (_startValve == value) return;
            _startValve = value;
            OnPropertyChanged(nameof(StartValve));
            OnPropertyChanged(nameof(ValveRangeHint));
        }
    }

    private int _fixtureSlotCapacity = 8;
    public int FixtureSlotCapacity { get => _fixtureSlotCapacity; set => SetLayoutField(ref _fixtureSlotCapacity, Math.Max(1, value)); }

    private int _fixtureCount = 8;
    public int FixtureCount { get => _fixtureCount; set => SetLayoutField(ref _fixtureCount, Math.Max(1, value)); }

    private int _startChannel = 1;
    public int StartChannel { get => _startChannel; set => SetLayoutField(ref _startChannel, value); }

    public int BoardCount => CurrentSlotLayout.BoardCount;

    public int LastBoard => CurrentSlotLayout.LastBoard;

    public int LastBoardSlot => CurrentSlotLayout.LastBoardSlot;

    public string BoardMappingSummary => CurrentSlotLayout.BoardMappingSummary;

    /// <summary>本次测试工位范围实际会用到的阀门（按工位自动推导，DMM 模式不使用压力阀）。</summary>
    public string ValveRangeHint => UsesFixedDmmSlotMap
        ? ""
        : $"{SlotLayoutHelper.ValveRangeText(_startIndex, _slotCount)}（工位 {_startIndex}-{_startIndex + _slotCount - 1}，每 32 工位一个阀）";

    private int _startSerial = 1;
    public int StartSerial { get => _startSerial; set => SetLayoutField(ref _startSerial, value); }

    private bool _autoNumber = true;
    public bool AutoNumber { get => _autoNumber; set => SetLayoutField(ref _autoNumber, value); }

    private bool _usePressure = true;
    public bool UsePressure { get => _usePressure; set => SetField(ref _usePressure, value); }

    private bool _useOven = true;
    public bool UseOven { get => _useOven; set => SetField(ref _useOven, value); }

    private bool _useLeakCheck = false;
    public bool UseLeakCheck { get => _useLeakCheck; set => SetField(ref _useLeakCheck, value); }

    private bool _useVentForGaugeZeroPressure = false;
    public bool UseVentForGaugeZeroPressure
    {
        get => _useVentForGaugeZeroPressure;
        set => SetField(ref _useVentForGaugeZeroPressure, value);
    }

    private bool _collectUt = true;
    public bool CollectUt { get => _collectUt; set => SetField(ref _collectUt, value); }

    private bool _collectUsc = true;
    public bool CollectUsc { get => _collectUsc; set => SetField(ref _collectUsc, value); }

    private bool _collectIsc = true;
    public bool CollectIsc { get => _collectIsc; set => SetField(ref _collectIsc, value); }

    private bool _collectUsg = true;
    public bool CollectUsg { get => _collectUsg; set => SetField(ref _collectUsg, value); }

    private bool _collectOvenTemp = true;
    public bool CollectOvenTemp { get => _collectOvenTemp; set => SetField(ref _collectOvenTemp, value); }

    public bool ShowAutoTestModeOptions => !_isLongTermStabilityMode;

    /// <summary>
    /// 「探漏」勾选框的可见性。万用表采集全程不控制气路阀门（气密性检测属电路板采集的活），
    /// 这里必须一起隐藏 —— 否则用户勾了却发现根本没跑，白折腾。
    /// </summary>
    public bool ShowLeakCheckOption => ShowAutoTestModeOptions && !UseDmmAutoTest;
    public string RunTaskScript => _isLongTermStabilityMode
        ? "Run:LongTermStabilityTest"
        : UseDmmAutoTest ? "Run:DmmAutoTest" : "Run:PerformanceTest";

    private bool _useDmmAutoTest;
    public bool UseDmmAutoTest
    {
        get => !_isLongTermStabilityMode && _useDmmAutoTest;
        set
        {
            if (_isLongTermStabilityMode) return;
            if (value == _useDmmAutoTest) return;
            _useDmmAutoTest = value;
            NormalizeFixedDmmLayout();
            OnPropertyChanged(nameof(UseDmmAutoTest));
            OnPropertyChanged(nameof(UseBoardAutoTest));
            OnPropertyChanged(nameof(ShowCollectionOptions));
            OnPropertyChanged(nameof(ShowDmmCollectionOptions));
            OnPropertyChanged(nameof(ShowBoardLayoutFields));
            OnPropertyChanged(nameof(ShowChannelHint));
            OnPropertyChanged(nameof(SlotLimit));
            OnPropertyChanged(nameof(DmmAutoTestSlotLimit));
            OnPropertyChanged(nameof(DmmAutoTestUnitCount));
            OnPropertyChanged(nameof(ChannelHint));
            OnPropertyChanged(nameof(PlanTaskPreview));
            Regenerate();
        }
    }

    public bool UseBoardAutoTest
    {
        get => !_isLongTermStabilityMode && !_useDmmAutoTest;
        set
        {
            if (value) UseDmmAutoTest = false;
        }
    }

    public bool ShowCollectionOptions => !_isLongTermStabilityMode && !UseDmmAutoTest;

    // ─── 万用表采集项：本轮是否采电压 / 电阻 / 烘箱温度 ────────────────────

    private bool _dmmCollectVoltage = true;
    public bool DmmCollectVoltage { get => _dmmCollectVoltage; set => SetField(ref _dmmCollectVoltage, value); }

    private bool _dmmCollectResistance = true;
    public bool DmmCollectResistance { get => _dmmCollectResistance; set => SetField(ref _dmmCollectResistance, value); }

    /// <summary>万用表模式下显示「电压 / 电阻 / 烘箱温度」勾选（电路板模式显示的是 UT/USC/ISC/USG）。</summary>
    public bool ShowDmmCollectionOptions => !_isLongTermStabilityMode && UseDmmAutoTest;

    public bool ShowLongTermMeasureOptions => _isLongTermStabilityMode;

    /// <summary>
    /// 长期稳定性与万用表自动测试都使用固定的 DAQ973A 通道映射，
    /// 都要在工位页显示通道提示（板卡模式没有固定通道，不显示）。
    /// </summary>
    public bool ShowChannelHint => _isLongTermStabilityMode || UseDmmAutoTest;

    /// <summary>烘箱选项可由操作员按现场设备情况自由启用或停用。</summary>
    public bool OvenRequiredForRun => false;

    private LongTermStabilityMeasureMode _longTermMeasureMode = LongTermStabilityMeasureMode.Voltage;
    public LongTermStabilityMeasureMode LongTermMeasureMode
    {
        get => _longTermMeasureMode;
        set
        {
            if (!_isLongTermStabilityMode || _longTermMeasureMode == value) return;
            _longTermMeasureMode = value;
            OnPropertyChanged(nameof(LongTermMeasureMode));
            OnPropertyChanged(nameof(LongTermMeasureVoltage));
            OnPropertyChanged(nameof(LongTermMeasureResistance));
            OnPropertyChanged(nameof(PlanTaskPreview));
        }
    }

    public bool LongTermMeasureVoltage
    {
        get => LongTermMeasureMode == LongTermStabilityMeasureMode.Voltage;
        set { if (value) LongTermMeasureMode = LongTermStabilityMeasureMode.Voltage; }
    }

    public bool LongTermMeasureResistance
    {
        get => LongTermMeasureMode == LongTermStabilityMeasureMode.Resistance;
        set { if (value) LongTermMeasureMode = LongTermStabilityMeasureMode.Resistance; }
    }

    private bool UsesFixedDmmSlotMap => _isLongTermStabilityMode || UseDmmAutoTest;

    public bool CanResumeCheckpoint { get; private set; }
    public string CheckpointSummary { get; private set; } = "";

    private string _resumeSoakMinutesText = "0";
    public string ResumeSoakMinutesText
    {
        get => _resumeSoakMinutesText;
        set => SetField(ref _resumeSoakMinutesText, value);
    }

    public int? ResumeSoakMinutesOverride =>
        ResumeFromCheckpoint && int.TryParse(ResumeSoakMinutesText, out var minutes)
            ? Math.Max(0, minutes)
            : null;

    private bool _resumeFromCheckpoint;
    public bool ResumeFromCheckpoint
    {
        get => _resumeFromCheckpoint;
        set => SetField(ref _resumeFromCheckpoint, value);
    }

    public ObservableCollection<SlotEntry> PreviewSlots { get; } = new();
    public int PreviewCount => PreviewSlots.Count;

    public RelayCommand RegenerateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool DialogResult { get; private set; }
    public event EventHandler? RequestClose;
    public event EventHandler? ScanResetRequested;

    public RunSetupViewModel(TestSession session, bool isLongTermStabilityMode = false)
    {
        _session = session;
        _isLongTermStabilityMode = isLongTermStabilityMode;
        if (_isLongTermStabilityMode)
            _startChannel = LongTermStabilitySlotMap.ChannelForSlot(1);

        LoadPlanFolders(session.Plan);
        SeedFromCurrentSlots(session.Slots);
        LoadReportDeviceOptions();
        if (_isLongTermStabilityMode)
        {
            _useOven = true;
        }
        DetectCheckpoint(session);
        Regenerate();

        RegenerateCommand = new RelayCommand(_ => ConfirmAndRegenerate());
        ApplyCommand      = new RelayCommand(_ => Apply(),  _ => SelectedPlan is not null && PreviewSlots.Count > 0);
        CancelCommand     = new RelayCommand(_ => { DialogResult = false; RequestClose?.Invoke(this, EventArgs.Empty); });
    }

    /// <summary>扫码录入需要下一行时扩展，不预生成占位行。</summary>
    public void EnsureSlotCount(int count)
    {
        var target = Math.Clamp(count, 1, SlotLimit);
        if (target <= PreviewSlots.Count) return;
        SlotCount = target;
    }

    private void NormalizeFixedDmmLayout()
    {
        if (!UsesFixedDmmSlotMap) return;

        _startIndex = Math.Clamp(_startIndex, 1, SlotLimit);
        _slotCount = Math.Clamp(_slotCount, 1, MaxSlotCountFromStart);
        _startChannel = _isLongTermStabilityMode
            ? LongTermStabilitySlotMap.ChannelForSlot(_startIndex)
            : DmmAutoSlotMap.VoltageChannelForSlot(_startIndex);

        OnPropertyChanged(nameof(StartIndex));
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(StartChannel));
        NotifySlotRangeChanged();
        NotifyBoardLayoutChanged();
    }

    private bool SetLayoutField<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        var changed = SetField(ref storage, value, name);
        if (changed)
        {
            NotifyBoardLayoutChanged();
            Regenerate();
        }
        return changed;
    }

    private void NotifyBoardLayoutChanged()
    {
        OnPropertyChanged(nameof(BoardCount));
        OnPropertyChanged(nameof(LastBoard));
        OnPropertyChanged(nameof(LastBoardSlot));
        OnPropertyChanged(nameof(BoardMappingSummary));
        OnPropertyChanged(nameof(ValveRangeHint));
    }

    private void DetectCheckpoint(TestSession session)
    {
        _checkpoint = null;
        CanResumeCheckpoint = false;
        CheckpointSummary = "";
        ResumeFromCheckpoint = false;

        if (!AppPreferences.SaveCheckpointOnAbort(session.Context.Settings) || !TestCheckpoint.Exists())
            return;

        var ck = TestCheckpoint.Load();
        if (ck is null || !ck.MatchesPlan(session.Plan.Name)) return;

        _checkpoint = ck;
        CanResumeCheckpoint = true;
        ResumeFromCheckpoint = true;
        CheckpointSummary =
            $"方案 {ck.PlanName}：温度点 {ck.TempIndex + 1}，压力点 {ck.PressureIndex + 1}，保存于 {ck.SavedAt:yyyy-MM-dd HH:mm}";
        OnPropertyChanged(nameof(CanResumeCheckpoint));
        OnPropertyChanged(nameof(CheckpointSummary));
    }

    private void UpdateCheckpointState(bool defaultResume = false)
    {
        var ck = _checkpoint;
        var canResume = ck is not null && SelectedPlan is not null && ck.MatchesPlan(SelectedPlan.Name);
        CanResumeCheckpoint = canResume;
        if (!canResume)
        {
            ResumeFromCheckpoint = false;
            CheckpointSummary = "";
        }
        else
        {
            if (defaultResume || !ResumeFromCheckpoint)
                ResumeFromCheckpoint = true;
            CheckpointSummary =
                $"方案 {ck!.PlanName}，温度点 {ck.TempIndex + 1}，压力点 {ck.PressureIndex + 1}，保存于 {ck.SavedAt:yyyy-MM-dd HH:mm}";
        }

        OnPropertyChanged(nameof(CanResumeCheckpoint));
        OnPropertyChanged(nameof(CheckpointSummary));
    }

    private void LoadPlanFolders(TestPlan current)
    {
        PlanFolders.Clear();

        if (Directory.Exists(AppPaths.TestConfigDir))
        {
            foreach (var dir in Directory.GetDirectories(AppPaths.TestConfigDir)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                PlanFolders.Add(Path.GetFileName(dir));
        }

        var folder = FindPlanFolder(current.Name)
            ?? (PlanFolders.Contains(current.FolderName) ? current.FolderName : null)
            ?? PlanFolders.FirstOrDefault()
            ?? "";

        if (string.IsNullOrWhiteSpace(folder))
        {
            SelectedPlan = current;
            return;
        }

        _selectedPlanFolder = folder;
        OnPropertyChanged(nameof(SelectedPlanFolder));
        LoadSensorModels(current.Name);
    }

    private string? FindPlanFolder(string sensorModel)
    {
        if (string.IsNullOrWhiteSpace(sensorModel) || !Directory.Exists(AppPaths.TestConfigDir))
            return null;

        foreach (var folder in PlanFolders)
        {
            var path = Path.Combine(AppPaths.TestConfigDir, folder, sensorModel + ".ini");
            if (File.Exists(path)) return folder;
        }
        return null;
    }

    private void LoadSensorModels(string? preferredSensor = null)
    {
        SensorModels.Clear();
        SelectedPlan = null;
        _selectedSensorModel = "";
        OnPropertyChanged(nameof(SelectedSensorModel));

        if (string.IsNullOrWhiteSpace(SelectedPlanFolder)) return;
        var folderPath = Path.Combine(AppPaths.TestConfigDir, SelectedPlanFolder);
        if (!Directory.Exists(folderPath)) return;

        foreach (var file in Directory.GetFiles(folderPath, "*.ini")
                     .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
            SensorModels.Add(Path.GetFileNameWithoutExtension(file));

        var sensor = !string.IsNullOrWhiteSpace(preferredSensor) && SensorModels.Contains(preferredSensor)
            ? preferredSensor
            : SensorModels.FirstOrDefault() ?? "";

        if (!string.IsNullOrWhiteSpace(sensor))
        {
            _selectedSensorModel = sensor;
            OnPropertyChanged(nameof(SelectedSensorModel));
            LoadSelectedPlan();
        }
    }

    private void LoadSelectedPlan()
    {
        if (string.IsNullOrWhiteSpace(SelectedPlanFolder) || string.IsNullOrWhiteSpace(SelectedSensorModel))
            return;

        var path = Path.Combine(AppPaths.TestConfigDir, SelectedPlanFolder, SelectedSensorModel + ".ini");
        if (!File.Exists(path)) return;

        try
        {
            SelectedPlan = TestPlan.Load(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("RunSetup", $"加载传感器型号失败 {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// 载入测试设备候选：先用内置默认 + 上次缓存的扫描结果把下拉框填上，
    /// 再在后台只读扫描往期报表 AO 列，扫完合并进候选（只读，不动目标目录里的任何文件）。
    /// </summary>
    private void LoadReportDeviceOptions()
    {
        foreach (var model in ReportDeviceCatalog.LoadCached(_session.Context.Settings, _reportDevice))
            ReportDeviceOptions.Add(model);

        var settings = _session.Context.Settings;
        Task.Run(() => ReportDeviceCatalog.ScanHistoricalDevices(settings))
            .ContinueWith(task =>
            {
                if (task.IsFaulted || task.Result.Count == 0) return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted) return;
                try
                {
                    dispatcher.Invoke(() => MergeReportDeviceOptions(task.Result));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("RunSetup", $"合并设备型号候选失败: {ex.Message}");
                }
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// 只追加缺失的候选（不做 Clear + 重填）：可编辑 ComboBox 在 Items 被清空时
    /// 会连带把编辑框里的输入清掉，用户刚敲的型号会被抹掉。
    /// </summary>
    private void MergeReportDeviceOptions(IEnumerable<string> models)
    {
        var added = false;
        foreach (var model in models)
        {
            var text = (model ?? "").Trim();
            if (text.Length == 0) continue;
            if (ReportDeviceOptions.Any(m => string.Equals(m, text, StringComparison.OrdinalIgnoreCase))) continue;
            ReportDeviceOptions.Add(text);
            added = true;
        }

        if (added) OnPropertyChanged(nameof(ReportDevice));
    }

    private void SeedFromCurrentSlots(SlotTable slots)
    {
        var ini = _session.Context.Settings;
        var hasUserSaved = !string.IsNullOrWhiteSpace(ini.Get("Slots", "LastPlan", ""));
        if (!_isLongTermStabilityMode)
            _useDmmAutoTest = ini.Get("Slots", "AutoTestMode", "Board").Equals("Dmm", StringComparison.OrdinalIgnoreCase);

        (_slotCount, _batchNo, _startIndex, _startBoard, _startBoardSlot,
         _boardSlotCapacity, _startValve, _fixtureSlotCapacity, _fixtureCount,
         _startChannel, _startSerial, _autoNumber) =
            CurrentSlotLayout.PatchedFromIni(ini, SlotLimit);

        if (_isLongTermStabilityMode)
        {
            var savedMode = ini.Get("Slots", "LongTermMeasureMode", "Voltage");
            _longTermMeasureMode = savedMode.Equals("Resistance", StringComparison.OrdinalIgnoreCase)
                ? LongTermStabilityMeasureMode.Resistance
                : LongTermStabilityMeasureMode.Voltage;
            _startChannel = LongTermStabilitySlotMap.ChannelForSlot(_startIndex);
            OnPropertyChanged(nameof(LongTermMeasureMode));
            OnPropertyChanged(nameof(LongTermMeasureVoltage));
            OnPropertyChanged(nameof(LongTermMeasureResistance));
            OnPropertyChanged(nameof(ChannelHint));
        }
        else if (UsesFixedDmmSlotMap)
            _startChannel = DmmAutoSlotMap.VoltageChannelForSlot(_startIndex);

        if (bool.TryParse(ini.Get("Slots", "UsePressure", ""), out var up)) _usePressure = up;
        if (bool.TryParse(ini.Get("Slots", "UseOven", ""), out var uo)) _useOven = uo;
        if (bool.TryParse(ini.Get("Slots", "UseLeakCheck", ""), out var ul)) _useLeakCheck = ul;
        // legacy vent setting intentionally ignored; gauge 0kPa uses normal pressure hold.
        _useVentForGaugeZeroPressure = false;
        if (bool.TryParse(ini.Get("Slots", "CollectUt", ""), out var cut)) _collectUt = cut;
        if (bool.TryParse(ini.Get("Slots", "CollectUsc", ""), out var cusc)) _collectUsc = cusc;
        if (bool.TryParse(ini.Get("Slots", "CollectIsc", ""), out var cisc)) _collectIsc = cisc;
        if (bool.TryParse(ini.Get("Slots", "CollectUsg", ""), out var cusg)) _collectUsg = cusg;
        if (bool.TryParse(ini.Get("Slots", "CollectOvenTemp", ""), out var cot)) _collectOvenTemp = cot;
        if (bool.TryParse(ini.Get("Slots", "DmmCollectVoltage", ""), out var dcv)) _dmmCollectVoltage = dcv;
        if (bool.TryParse(ini.Get("Slots", "DmmCollectResistance", ""), out var dcr)) _dmmCollectResistance = dcr;

        _reportDevice = ini.Get("Report", "DeviceName", ini.Get("Device.Pressure", "Model", ""));
        _reportWaferNo = ini.Get("Report", "WaferNo", "");
        _reportModel = ini.Get("Report", "Model", "");
        _selectedReportTemplate = TemplatePerformanceExporter.ResolveTemplateMode(ini) == ReportTemplateMode.Wafer
            ? WaferTemplateLabel
            : AutoTemplateLabel;
        OnPropertyChanged(nameof(ReportDevice));
        OnPropertyChanged(nameof(ReportWaferNo));
        OnPropertyChanged(nameof(ReportModel));
        OnPropertyChanged(nameof(SelectedReportTemplate));
        OnPropertyChanged(nameof(ReportTemplateHint));

        _configSerials = SlotLayoutHelper.CollectSerialMap(slots.Entries);
        _configPositions = SlotLayoutHelper.CollectPositions(slots.Entries);
        var filledCount = SlotLayoutHelper.CountFilledSlots(slots.Entries);
        if (filledCount > 0)
        {
            // 优先采用配置页已扫码录入的工位数
            _slotCount = Math.Clamp(filledCount, 1, SlotLimit);
        }
        else if (!hasUserSaved && slots.Entries.Count > 0)
        {
            _slotCount = Math.Min(slots.Entries.Count, SlotLimit);
            var first = slots.Entries[0].SerialNo;
            var idx = first.LastIndexOf('_');
            if (idx > 0) _batchNo = first[..idx];
        }

        // 现有工位表的第一行决定起始工位：表里已经是 Slot129 就别再退回 1，
        // 否则重新生成会把工位号重置成 1-64，阀门也跟着错回 1、2 号阀。
        if (slots.Entries.Count > 0)
            _startIndex = Math.Clamp(SlotDacAddress.ParseSlotIndex(slots.Entries[0].Slot), 1, SlotLimit);

        NormalizeFixedDmmLayout();
        SyncDerivedLayoutFromStartIndex();
    }

    /// <summary>当前布局参数快照（公式计算 / 批量生成 / ini 持久化共用，与 ConfigViewModel 一致）。</summary>
    private SlotLayoutSnapshot CurrentSlotLayout => new(
        SlotCount: _slotCount,
        BatchNo: _batchNo,
        StartIndex: _startIndex,
        StartBoard: _startBoard,
        StartBoardSlot: _startBoardSlot,
        BoardSlotCapacity: _boardSlotCapacity,
        StartValve: _startValve,
        FixtureSlotCapacity: _fixtureSlotCapacity,
        FixtureCount: _fixtureCount,
        StartChannel: _startChannel,
        StartSerial: _startSerial,
        AutoNumber: _autoNumber);

    private SlotLayoutOptions BuildSlotLayoutOptions() => CurrentSlotLayout.ToOptions();

    public void Regenerate()
    {
        Regenerate(preserveSerials: true);
    }

    private void ConfirmAndRegenerate()
    {
        var message = "刷新工位会重新生成全部工位，并清空当前序列号，方便重新扫码录入。\n\n确定要刷新吗？";
        if (MessageBox.Show(message, "刷新工位", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Regenerate(preserveSerials: false);
        ScanResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Regenerate(bool preserveSerials)
    {
        var preserved = preserveSerials
            ? SlotLayoutHelper.CollectSerialMap(PreviewSlots)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preservedPositions = preserveSerials
            ? SlotLayoutHelper.CollectPositions(PreviewSlots)
            : new Dictionary<string, (string X, string Y)>(StringComparer.OrdinalIgnoreCase);
        if (preserveSerials && (!UsesFixedDmmSlotMap || !AutoNumber))
        {
            SlotLayoutHelper.MergeSerialMaps(preserved, _configSerials);
            SlotLayoutHelper.MergePositions(preservedPositions, _configPositions);
        }

        NormalizeFixedDmmLayout();
        SyncDerivedLayoutFromStartIndex();

        var generated = SlotLayoutHelper.Generate(BuildSlotLayoutOptions());
        if (_isLongTermStabilityMode)
            generated = ApplyLongTermStabilityChannels(generated, _startIndex);
        else if (UseDmmAutoTest)
            generated = ApplyDmmAutoTestChannels(generated, _startIndex);
        else
            generated = FillPressureValveAddresses(generated);
        if (preserveSerials)
        {
            SlotLayoutHelper.ApplyPreservedSerials(generated, preserved);
            SlotLayoutHelper.ApplyPreservedPositions(generated, preservedPositions);
        }
        else
        {
            foreach (var slot in generated)
                slot.SerialNo = "";
        }

        PreviewSlots.Clear();
        foreach (var s in generated) PreviewSlots.Add(s);

        OnPropertyChanged(nameof(PreviewCount));
        OnPropertyChanged(nameof(ReportWaferNoHint));
    }

    private static List<SlotEntry> ApplyLongTermStabilityChannels(List<SlotEntry> slots, int startSlotNo)
    {
        var count = Math.Min(slots.Count, LongTermStabilitySlotMax - startSlotNo + 1);
        var result = new List<SlotEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var slotNo = startSlotNo + i;
            result.Add(slots[i] with
            {
                Slot = $"Slot{slotNo}",
                Board = (((slotNo - 1) / 20) + 1).ToString(),
                BoardSlotNo = (((slotNo - 1) % 20) + 1).ToString(),
                Channel = LongTermStabilitySlotMap.ChannelForSlot(slotNo).ToString(),
                Dmm = "DAQ970A/DAQ973A"
            });
        }
        return result;
    }

    /// <summary>
    /// 万用表采集模式的工位映射：按 16 工位一组分到 973-1 / 973-2 / 973-3。
    /// 「数字万用表」列写设备号、「通道」列写电压通道、「电源开关」列写 301-316；
    /// 气路阀门相关列（阀位 / 阀门）一律清空 —— 万用表模式不控制阀门。
    /// </summary>
    private static List<SlotEntry> ApplyDmmAutoTestChannels(List<SlotEntry> slots, int startSlotNo)
    {
        var count = Math.Min(slots.Count, DmmAutoSlotMap.MaxSlotCount - startSlotNo + 1);
        var result = new List<SlotEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var slotNo = startSlotNo + i;
            var unit = DmmAutoSlotMap.UnitIndexForSlot(slotNo);
            var local = DmmAutoSlotMap.LocalIndexForSlot(slotNo);
            result.Add(slots[i] with
            {
                Slot = $"Slot{slotNo}",
                Valve = "",
                ValveAddr = "",
                Board = "DMM",
                BoardSlotNo = (local + 1).ToString(CultureInfo.InvariantCulture),
                Channel = DmmAutoSlotMap.VoltageChannelForSlot(slotNo).ToString(CultureInfo.InvariantCulture),
                Dmm = $"973-{unit + 1}",
                PowerSwitch = DmmAutoSlotMap.PowerSwitchChannelForSlot(slotNo).ToString(CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    /// <summary>
    /// 压力测试模式：把每个工位实际会用到的阀门继电器通道写进「阀门」列（ValveAddr），
    /// 让操作员在预览里就能核对（例如工位 129-192 → 阀 5/6 → 通道 106/107）。
    /// 运行时 BuiltinActions 用同一个 SlotLayoutHelper.ValveForSlot 解析，两者不会漂移。
    /// </summary>
    private List<SlotEntry> FillPressureValveAddresses(List<SlotEntry> slots)
    {
        var settings = _session.Context.Settings;
        var result = new List<SlotEntry>(slots.Count);
        foreach (var slot in slots)
        {
            var valveNo = SlotLayoutHelper.ValveForSlot(SlotDacAddress.ParseSlotIndex(slot.Slot));
            var address = settings.Get("ValveSettings", $"Valve{valveNo}", "");
            result.Add(slot with
            {
                Valve = valveNo.ToString(),
                ValveAddr = string.IsNullOrWhiteSpace(address) ? "-" : address.Trim(),
            });
        }
        return result;
    }

    private static double PressureRateToPa(double value, string? unit) =>
        (unit ?? "").Trim().ToUpperInvariant() switch
        {
            "MPA" => value * 1_000_000.0,
            "KPA" => value * 1_000.0,
            "PA" => value,
            _ => value
        };

    private void Apply()
    {
        if (SelectedPlan is null) return;

        var list = SlotLayoutHelper.TrimTrailingPlaceholders(PreviewSlots.ToList());
        var newSlots = new SlotTable(list);
        _session.ApplyRunConfig(SelectedPlan, newSlots);

        try
        {
            var ini = _session.Context.Settings;
            CurrentSlotLayout.SaveToIni(ini);
            ini.Set("Slots", "LastPlan", SelectedPlan.Name);
            ini.Set("Slots", "LastPlanFolder", SelectedPlanFolder);
            ini.Set("Slots", "UsePressure", UsePressure.ToString());
            ini.Set("Slots", "UseOven", UseOven.ToString());
            ini.Set("Slots", "UseLeakCheck", UseLeakCheck.ToString());
            ini.Set("Slots", "UseVentForGaugeZeroPressure", UseVentForGaugeZeroPressure.ToString());
            ini.Set("Slots", "CollectUt", CollectUt.ToString());
            ini.Set("Slots", "CollectUsc", CollectUsc.ToString());
            ini.Set("Slots", "CollectIsc", CollectIsc.ToString());
            ini.Set("Slots", "CollectUsg", CollectUsg.ToString());
            ini.Set("Slots", "CollectOvenTemp", CollectOvenTemp.ToString());
            ini.Set("Slots", "DmmCollectVoltage", DmmCollectVoltage.ToString());
            ini.Set("Slots", "DmmCollectResistance", DmmCollectResistance.ToString());
            ini.Set("Slots", "AutoTestMode", UseDmmAutoTest ? "Dmm" : "Board");
            ini.Set("Report", "DeviceName", (ReportDevice ?? "").Trim());
            ini.Set("Report", "WaferNo", (ReportWaferNo ?? "").Trim());
            ini.Set("Report", "Model", (ReportModel ?? "").Trim());
            ini.Set("Report", "Template", TemplatePerformanceExporter.TemplateModeKey(SelectedTemplateMode));
            if (_isLongTermStabilityMode)
                ini.Set("Slots", "LongTermMeasureMode", LongTermMeasureMode.ToString());
            AppPreferences.Set(ini, "LastPlan", SelectedPlan.Name);
            AppPreferences.Set(ini, "LastPlanFolder", SelectedPlanFolder);
            ini.Save(AppPaths.SettingIni);
        }
        catch (Exception ex) { AppLog.Warn("RunSetup", $"保存布局参数失败: {ex.Message}"); }

        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
