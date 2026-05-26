using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
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

    // ─── Plan selection ────────────────────────────────────────────────────
    public ObservableCollection<TestPlan> Plans { get; } = new();

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
                OnPropertyChanged(nameof(PlanTaskPreview));
            }
        }
    }

    public string PlanSensorType    => _selectedPlan?.SensorType ?? "—";
    public string PlanPressureUnit  => _selectedPlan?.PressureUnit ?? "—";
    public string PlanPrecision     => _selectedPlan is null ? "—" : $"{_selectedPlan.Precision}";
    public int    PlanPressureCount => _selectedPlan?.PressurePoints.Count ?? 0;
    public int    PlanTempCount     => _selectedPlan?.TempPoints.Count ?? 0;
    public string PlanTaskPreview
    {
        get
        {
            var plan = _selectedPlan;
            if (plan is null) return "";
            var s = plan.TaskScript ?? "";
            if (s.Length == 0) return "";

            // If the script is the single Run:PerformanceTest command,
            // expand it into a human-readable step list based on the plan.
            var cmds = s.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            if (cmds.Count == 1 && cmds[0].Equals("Run:PerformanceTest", StringComparison.OrdinalIgnoreCase))
                return ExpandPerformancePreview(plan);

            // Pretty-print one command per line.
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
                lines.Add($"    ── 压力点 {pp.Name} = {pp.Value} {plan.PressureUnit}");
                lines.Add($"       设置压力 → 稳定 → 保压");
                lines.Add($"       逐工位采集 USC, ISC, USG");
            }
            lines.Add("");
        }

        lines.Add("【结束】保存数据 → 泄压");
        return string.Join(Environment.NewLine, lines);
    }

    // ─── Slot layout inputs ────────────────────────────────────────────────
    public const int SlotMax = 256;

    private int _slotCount = 16;
    public int SlotCount
    {
        get => _slotCount;
        set => SetLayoutField(ref _slotCount, Math.Clamp(value, 1, SlotMax));
    }

    private string _batchNo = $"{DateTime.Now:yyMMdd}_01";
    public string BatchNo { get => _batchNo; set => SetLayoutField(ref _batchNo, value); }

    private int _startIndex = 1;
    public int StartIndex { get => _startIndex; set => SetLayoutField(ref _startIndex, value); }

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

    private bool _useLeakCheck = true;
    public bool UseLeakCheck { get => _useLeakCheck; set => SetField(ref _useLeakCheck, value); }

    public ObservableCollection<SlotEntry> PreviewSlots { get; } = new();
    public int PreviewCount => PreviewSlots.Count;

    // ─── Commands ──────────────────────────────────────────────────────────
    public RelayCommand RegenerateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool DialogResult { get; private set; }
    public event EventHandler? RequestClose;

    public RunSetupViewModel(TestSession session)
    {
        _session = session;

        LoadPlans(session.Plan);
        SeedFromCurrentSlots(session.Slots);
        Regenerate();

        RegenerateCommand = new RelayCommand(_ => Regenerate());
        ApplyCommand      = new RelayCommand(_ => Apply(),  _ => SelectedPlan is not null && PreviewSlots.Count > 0);
        CancelCommand     = new RelayCommand(_ => { DialogResult = false; RequestClose?.Invoke(this, EventArgs.Empty); });
    }

    private bool SetLayoutField<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        var changed = SetField(ref storage, value, name);
        if (changed)
            Regenerate();
        return changed;
    }

    private void LoadPlans(TestPlan current)
    {
        // Always include the currently-loaded plan so the dialog is usable even
        // without TestConfig\*.ini files.
        Plans.Add(current);

        if (Directory.Exists(AppPaths.TestConfigDir))
        {
            foreach (var p in Directory.GetFiles(AppPaths.TestConfigDir, "*.ini"))
            {
                try
                {
                    var plan = TestPlan.Load(p);
                    if (Plans.All(x => !string.Equals(x.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
                        Plans.Add(plan);
                }
                catch (Exception ex) { AppLog.Warn("RunSetup", $"加载方案失败 {p}: {ex.Message}"); }
            }
        }

        SelectedPlan = Plans.FirstOrDefault(p => p.Name == current.Name) ?? Plans.FirstOrDefault();
    }

    private void SeedFromCurrentSlots(SlotTable slots)
    {
        var ini = _session.Context.Settings;

        // 从 Setting.ini 恢复上次 Apply 保存的布局参数
        var hasUserSaved = !string.IsNullOrWhiteSpace(ini.Get("Slots", "LastPlan", ""));

        if (int.TryParse(ini.Get("Slots", "Count", ""), out var savedCount) && savedCount > 0)
            _slotCount = Math.Clamp(savedCount, 1, SlotMax);
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
        if (int.TryParse(ini.Get("Slots", "StartSerial", ""), out var ss)) _startSerial = ss;

        // 只有在 ini 中没有用户保存的配置时，才用 session.Slots 覆盖
        // （session.Slots 可能来自 App 启动时的默认初始化，不一定是用户想要的）
        if (!hasUserSaved && slots.Entries.Count > 0)
        {
            _slotCount = Math.Min(slots.Entries.Count, SlotMax);
            var first = slots.Entries[0].SerialNo;
            var idx = first.LastIndexOf('_');
            if (idx > 0) _batchNo = first[..idx];
        }
    }

    /// <summary>Regenerate <see cref="PreviewSlots"/> from the layout inputs.</summary>
    public void Regenerate()
    {
        PreviewSlots.Clear();

        int board = StartBoard;
        int boardSlot = StartBoardSlot;
        int valve = StartValve;
        int fixtureSlot = 1;
        int fixture = 1;
        int layer = 1;
        int channel = StartChannel;
        int serial = StartSerial;

        for (int i = 0; i < SlotCount; i++)
        {
            string slotName = $"Slot{i + 1}";
            string serialNo = AutoNumber
                ? $"{BatchNo}#{serial}"
                : $"{BatchNo}#{i + StartIndex}";

            PreviewSlots.Add(new SlotEntry(
                Slot: slotName,
                SerialNo: serialNo,
                Valve: valve.ToString(),
                Board: board.ToString(),
                BoardSlotNo: boardSlot.ToString(),
                Layer: layer.ToString(),
                Fixture: fixture.ToString(),
                FixtureSlotNo: fixtureSlot.ToString(),
                PressureController: "1",
                Dmm: "-",
                Channel: channel.ToString(),
                ValveAddr: "-"));

            // Advance counters with rollover semantics matching ASLab's layout sheet.
            // 1 valve = 32 slots, 2 valves per layer, 4 layers total
            serial++;
            boardSlot++;
            if (boardSlot > BoardSlotCapacity) { boardSlot = 1; board++; }
            fixtureSlot++;
            if (fixtureSlot > FixtureSlotCapacity) { fixtureSlot = 1; fixture++; }
            if (fixture > FixtureCount) { fixture = 1; layer++; }
            channel++;
            // Valve advances every 32 slots
            if ((i + 1) % 32 == 0) valve++;
        }

        OnPropertyChanged(nameof(PreviewCount));
    }

    private void Apply()
    {
        if (SelectedPlan is null) return;

        Regenerate();
        var newSlots = new SlotTable(PreviewSlots.ToList());
        _session.ApplyRunConfig(SelectedPlan, newSlots);

        // 保存布局参数到 Setting.ini，下次打开可恢复
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
            ini.Set("Slots", "LastPlan", SelectedPlan.Name);
            ini.Save(AppPaths.SettingIni);
        }
        catch (Exception ex) { AppLog.Warn("RunSetup", $"保存布局参数失败: {ex.Message}"); }

        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
