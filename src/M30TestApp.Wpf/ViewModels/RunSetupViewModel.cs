using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

    private static string ExpandLongTermStabilityPreview(TestPlan plan)
    {
        var lines = new List<string>();
        lines.Add("═══ Run:LongTermStabilityTest 长期稳定性测试 ═══");
        lines.Add("");
        lines.Add("只使用 DAQ973A / 数字万用表采集电压，不采集 UT / USC / ISC / USG / 电阻。");
        lines.Add("");

        for (var ti = 0; ti < plan.TempPoints.Count; ti++)
        {
            var tp = plan.TempPoints[ti];
            var soak = tp.SoakMinutes ?? 120;
            lines.Add($"【{ti + 1}】温度点 {tp.Name} = {tp.Celsius} °C");
            lines.Add($"    设置烘箱温度 → 等待到达 → 保温 {soak} min");

            foreach (var pp in plan.PressurePoints)
            {
                lines.Add($"    ── 压力点 {pp.Name} = {pp.Value} {plan.PressureUnit} [{pp.PressureTypeDisplay}]");
                lines.Add("       设置压力 → 稳定 → 保压 → DAQ973A 逐工位采集电压");
            }
            lines.Add("");
        }

        lines.Add("【结束】保存数据 → 泄压");
        return string.Join(Environment.NewLine, lines);
    }

    private static string ExpandDmmAutoTestPreview(TestPlan plan)
    {
        var lines = new List<string>();
        lines.Add("Run:DmmAutoTest 万用表自动测试");
        lines.Add("");
        lines.Add("工位上限 16；电压通道 101-116；电阻通道 201-216；切换通道预留 301-304。");
        lines.Add("保温、保压、压力点和温度点流程与自动测试一致。");
        lines.Add("电压写入 USG 指标列，电阻写入 DMM_R 并优先用于 TCR。");
        lines.Add("");

        foreach (var tp in plan.TempPoints)
        {
            var soak = tp.SoakMinutes ?? 120;
            lines.Add($"温度点 {tp.Name} = {tp.Celsius} C");
            lines.Add($"    设置烘箱 -> 等待到达 -> 保温 {soak} min -> 采集电阻 DMM_R");
            foreach (var pp in plan.PressurePoints)
                lines.Add($"    压力点 {pp.Name} = {pp.Value} {plan.PressureUnit} -> 稳定 -> 保压 -> 采集电压 USG");
            lines.Add("    下行回差压力点 -> 采集电压 USG_R");
            lines.Add("");
        }

        lines.Add("结束：计算指标 -> 保存数据 -> 泄压");
        return string.Join(Environment.NewLine, lines);
    }

    // ─── Slot layout inputs ────────────────────────────────────────────────
    public const int SlotMax = SlotLayoutHelper.SlotMax;
    public const int LongTermStabilitySlotMax = 60;
    public const int DmmAutoTestSlotMax = 16;
    public int SlotLimit => _isLongTermStabilityMode
        ? LongTermStabilitySlotMax
        : UseDmmAutoTest ? DmmAutoTestSlotMax : SlotMax;
    public string ChannelHint => _isLongTermStabilityMode
        ? "DAQ channels: 101-120 / 201-220 / 301-320"
        : UseDmmAutoTest ? "DMM channels: V=101-116 / R=201-216 / Switch=301-304"
        : "";

    private int _slotCount = 16;
    public int SlotCount
    {
        get => _slotCount;
        set => SetLayoutField(ref _slotCount, Math.Clamp(value, 1, MaxSlotCountFromStart));
    }

    private string _batchNo = $"{DateTime.Now:yyMMdd}_01";
    public string BatchNo { get => _batchNo; set => SetLayoutField(ref _batchNo, value); }

    private int _startIndex = 1;
    public int StartIndex
    {
        get => _startIndex;
        set
        {
            var next = UsesFixedDmmSlotMap
                ? Math.Clamp(value, 1, SlotLimit)
                : value;
            if (!SetField(ref _startIndex, next)) return;
            if (UsesFixedDmmSlotMap && _slotCount > MaxSlotCountFromStart)
            {
                _slotCount = MaxSlotCountFromStart;
                OnPropertyChanged(nameof(SlotCount));
            }
            Regenerate();
        }
    }

    private int MaxSlotCountFromStart => UsesFixedDmmSlotMap
        ? Math.Max(1, SlotLimit - Math.Clamp(_startIndex, 1, SlotLimit) + 1)
        : SlotLimit;

    private int _startBoard = 1;
    public int StartBoard { get => _startBoard; set => SetLayoutField(ref _startBoard, value); }

    private int _startBoardSlot = 1;
    public int StartBoardSlot { get => _startBoardSlot; set => SetLayoutField(ref _startBoardSlot, value); }

    private int _boardSlotCapacity = 16;
    public int BoardSlotCapacity { get => _boardSlotCapacity; set => SetLayoutField(ref _boardSlotCapacity, Math.Max(1, value)); }

    private int _startValve = 1;
    public int StartValve { get => _startValve; set => SetLayoutField(ref _startValve, value); }

    private int _fixtureSlotCapacity = 8;
    public int FixtureSlotCapacity { get => _fixtureSlotCapacity; set => SetLayoutField(ref _fixtureSlotCapacity, Math.Max(1, value)); }

    private int _fixtureCount = 8;
    public int FixtureCount { get => _fixtureCount; set => SetLayoutField(ref _fixtureCount, Math.Max(1, value)); }

    private int _startChannel = 1;
    public int StartChannel { get => _startChannel; set => SetLayoutField(ref _startChannel, value); }

    private int _startSerial = 1;
    public int StartSerial { get => _startSerial; set => SetLayoutField(ref _startSerial, value); }

    private bool _autoNumber = true;
    public bool AutoNumber { get => _autoNumber; set => SetLayoutField(ref _autoNumber, value); }

    private bool _usePressure = true;
    public bool UsePressure { get => _usePressure; set => SetField(ref _usePressure, value); }

    private bool _useOven = true;
    public bool UseOven { get => _useOven; set => SetField(ref _useOven, value); }

    private bool _usePower = true;
    public bool UsePower { get => _usePower; set => SetField(ref _usePower, value); }

    private bool _useLeakCheck = false;
    public bool UseLeakCheck { get => _useLeakCheck; set => SetField(ref _useLeakCheck, value); }

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
            OnPropertyChanged(nameof(SlotLimit));
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

    public RunSetupViewModel(TestSession session, bool isLongTermStabilityMode = false)
    {
        _session = session;
        _isLongTermStabilityMode = isLongTermStabilityMode;
        if (_isLongTermStabilityMode)
            _startChannel = 101;

        LoadPlanFolders(session.Plan);
        SeedFromCurrentSlots(session.Slots);
        if (_isLongTermStabilityMode)
            _usePower = false;
        DetectCheckpoint(session);
        Regenerate();

        RegenerateCommand = new RelayCommand(_ => Regenerate());
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
            ? LongTermChannelForSlot(_startIndex)
            : DmmAutoVoltageChannelForSlot(_startIndex);

        OnPropertyChanged(nameof(StartIndex));
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(StartChannel));
    }

    private bool SetLayoutField<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        var changed = SetField(ref storage, value, name);
        if (changed)
            Regenerate();
        return changed;
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

    private void SeedFromCurrentSlots(SlotTable slots)
    {
        var ini = _session.Context.Settings;
        var hasUserSaved = !string.IsNullOrWhiteSpace(ini.Get("Slots", "LastPlan", ""));
        if (!_isLongTermStabilityMode)
            _useDmmAutoTest = ini.Get("Slots", "AutoTestMode", "Board").Equals("Dmm", StringComparison.OrdinalIgnoreCase);

        if (int.TryParse(ini.Get("Slots", "Count", ""), out var savedCount) && savedCount > 0)
            _slotCount = Math.Clamp(savedCount, 1, SlotLimit);
        var savedBatch = ini.Get("Slots", "BatchNo", "");
        if (!string.IsNullOrWhiteSpace(savedBatch)) _batchNo = savedBatch;
        if (int.TryParse(ini.Get("Slots", "StartIndex", ""), out var si)) _startIndex = si;
        if (int.TryParse(ini.Get("Slots", "StartBoard", ""), out var sb)) _startBoard = sb;
        if (int.TryParse(ini.Get("Slots", "StartBoardSlot", ""), out var sbs)) _startBoardSlot = sbs;
        if (int.TryParse(ini.Get("Slots", "BoardSlotCapacity", ""), out var bsc) && bsc > 0) _boardSlotCapacity = bsc;
        if (int.TryParse(ini.Get("Slots", "StartValve", ""), out var sv)) _startValve = sv;
        if (int.TryParse(ini.Get("Slots", "FixtureSlotCapacity", ""), out var fsc) && fsc > 0) _fixtureSlotCapacity = fsc;
        if (int.TryParse(ini.Get("Slots", "FixtureCount", ""), out var fc) && fc > 0) _fixtureCount = fc;
        if (int.TryParse(ini.Get("Slots", "StartChannel", ""), out var sc)) _startChannel = sc;
        if (UsesFixedDmmSlotMap) _startChannel = _isLongTermStabilityMode ? 101 : 101;
        if (int.TryParse(ini.Get("Slots", "StartSerial", ""), out var ss)) _startSerial = ss;
        if (bool.TryParse(ini.Get("Slots", "AutoNumber", ""), out var an)) _autoNumber = an;

        if (bool.TryParse(ini.Get("Slots", "UsePressure", ""), out var up)) _usePressure = up;
        if (bool.TryParse(ini.Get("Slots", "UseOven", ""), out var uo)) _useOven = uo;
        if (bool.TryParse(ini.Get("Slots", "UsePower", ""), out var uw)) _usePower = uw;
        if (bool.TryParse(ini.Get("Slots", "UseLeakCheck", ""), out var ul)) _useLeakCheck = ul;
        if (bool.TryParse(ini.Get("Slots", "CollectUt", ""), out var cut)) _collectUt = cut;
        if (bool.TryParse(ini.Get("Slots", "CollectUsc", ""), out var cusc)) _collectUsc = cusc;
        if (bool.TryParse(ini.Get("Slots", "CollectIsc", ""), out var cisc)) _collectIsc = cisc;
        if (bool.TryParse(ini.Get("Slots", "CollectUsg", ""), out var cusg)) _collectUsg = cusg;
        if (bool.TryParse(ini.Get("Slots", "CollectOvenTemp", ""), out var cot)) _collectOvenTemp = cot;

        _configSerials = SlotLayoutHelper.CollectSerialMap(slots.Entries);
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

        NormalizeFixedDmmLayout();
    }

    private SlotLayoutOptions BuildSlotLayoutOptions() => new(
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

    public void Regenerate()
    {
        var preserved = SlotLayoutHelper.CollectSerialMap(PreviewSlots);
        if (!UsesFixedDmmSlotMap || !AutoNumber)
            SlotLayoutHelper.MergeSerialMaps(preserved, _configSerials);

        NormalizeFixedDmmLayout();

        var generated = SlotLayoutHelper.Generate(BuildSlotLayoutOptions());
        if (_isLongTermStabilityMode)
            generated = ApplyLongTermStabilityChannels(generated, _startIndex);
        else if (UseDmmAutoTest)
            generated = ApplyDmmAutoTestChannels(generated, _startIndex);
        SlotLayoutHelper.ApplyPreservedSerials(generated, preserved);

        PreviewSlots.Clear();
        foreach (var s in generated) PreviewSlots.Add(s);

        OnPropertyChanged(nameof(PreviewCount));
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
                Channel = LongTermChannelForSlot(slotNo).ToString(),
                Dmm = "DAQ970A/DAQ973A"
            });
        }
        return result;
    }

    private static int LongTermChannelForSlot(int slotNo) =>
        (((slotNo - 1) / 20) + 1) * 100 + (((slotNo - 1) % 20) + 1);

    private static List<SlotEntry> ApplyDmmAutoTestChannels(List<SlotEntry> slots, int startSlotNo)
    {
        var count = Math.Min(slots.Count, DmmAutoTestSlotMax - startSlotNo + 1);
        var result = new List<SlotEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var slotNo = startSlotNo + i;
            result.Add(slots[i] with
            {
                Slot = $"Slot{slotNo}",
                Board = "DMM",
                BoardSlotNo = slotNo.ToString(),
                Channel = DmmAutoVoltageChannelForSlot(slotNo).ToString(),
                Dmm = "DAQ970A/DAQ973A",
                ValveAddr = DmmAutoSwitchChannelForSlot(slotNo).ToString()
            });
        }
        return result;
    }

    private static int DmmAutoVoltageChannelForSlot(int slotNo) => 100 + slotNo;
    private static int DmmAutoSwitchChannelForSlot(int slotNo) => 301 + Math.Clamp((slotNo - 1) / 4, 0, 3);

    private void Apply()
    {
        if (SelectedPlan is null) return;

        var list = SlotLayoutHelper.TrimTrailingPlaceholders(PreviewSlots.ToList());
        var newSlots = new SlotTable(list);
        _session.ApplyRunConfig(SelectedPlan, newSlots);

        try
        {
            var ini = _session.Context.Settings;
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
            ini.Set("Slots", "LastPlan", SelectedPlan.Name);
            ini.Set("Slots", "LastPlanFolder", SelectedPlanFolder);
            ini.Set("Slots", "UsePressure", UsePressure.ToString());
            ini.Set("Slots", "UseOven", UseOven.ToString());
            if (!_isLongTermStabilityMode)
                ini.Set("Slots", "UsePower", UsePower.ToString());
            ini.Set("Slots", "UseLeakCheck", UseLeakCheck.ToString());
            ini.Set("Slots", "CollectUt", CollectUt.ToString());
            ini.Set("Slots", "CollectUsc", CollectUsc.ToString());
            ini.Set("Slots", "CollectIsc", CollectIsc.ToString());
            ini.Set("Slots", "CollectUsg", CollectUsg.ToString());
            ini.Set("Slots", "CollectOvenTemp", CollectOvenTemp.ToString());
            ini.Set("Slots", "AutoTestMode", UseDmmAutoTest ? "Dmm" : "Board");
            AppPreferences.Set(ini, "LastPlan", SelectedPlan.Name);
            AppPreferences.Set(ini, "LastPlanFolder", SelectedPlanFolder);
            ini.Save(AppPaths.SettingIni);
        }
        catch (Exception ex) { AppLog.Warn("RunSetup", $"保存布局参数失败: {ex.Message}"); }

        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
