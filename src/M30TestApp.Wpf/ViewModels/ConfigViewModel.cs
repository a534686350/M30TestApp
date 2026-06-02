using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;
using M30TestApp.Wpf.Themes;

namespace M30TestApp.Wpf.ViewModels;

public sealed class MetricSwitch : ViewModelBase
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private SpecRange? _spec;
    public string Min
    {
        get => _spec?.Min ?? "";
        set
        {
            if (_spec is null || _spec.Min == value) return;
            _spec.Min = value;
            OnPropertyChanged();
        }
    }

    public string Max
    {
        get => _spec?.Max ?? "";
        set
        {
            if (_spec is null || _spec.Max == value) return;
            _spec.Max = value;
            OnPropertyChanged();
        }
    }

    public void BindSpec(SpecRange spec)
    {
        _spec = spec;
        OnPropertyChanged(nameof(Min));
        OnPropertyChanged(nameof(Max));
    }
}

public sealed class CommandTemplateVm : ViewModelBase
{
    public string Action { get; init; } = "";

    private string _template = "";
    public string Template { get => _template; set => SetField(ref _template, value); }
}

public sealed class ModelCommandsVm
{
    public string Kind { get; init; } = "";
    public string Model { get; init; } = "";
    public ObservableCollection<CommandTemplateVm> Templates { get; } = new();
}

public sealed class TaskStepVm : ViewModelBase
{
    private int _index;
    public int Index { get => _index; set => SetField(ref _index, value); }

    public string Text { get; init; } = "";
    public string Module => Text.Split(':') is { Length: >= 1 } parts ? parts[0] : "";
}

public sealed class SettingPairVm : ViewModelBase
{
    public SettingPairVm(string name, string value, IEnumerable<string>? options = null, string unit = "", string section = "", string key = "")
    {
        Name = name;
        _value = value;
        Unit = unit;
        Section = section;
        Key = string.IsNullOrWhiteSpace(key) ? name : key;
        Options = options is null ? new ObservableCollection<string>() : new ObservableCollection<string>(options);
    }

    public string Name { get; }
    public string Unit { get; }
    public string Section { get; }
    public string Key { get; }
    public ObservableCollection<string> Options { get; }
    public bool HasOptions => Options.Count > 0;

    private string _value;
    public string Value { get => _value; set => SetField(ref _value, value); }
}

public sealed class PressureCommandSettingVm : ViewModelBase
{
    public PressureCommandSettingVm(string name, string command)
    {
        Name = name;
        _command = command;
    }

    public string Name { get; }
    private string _command;
    public string Command { get => _command; set => SetField(ref _command, value); }
}

public sealed class ConfigViewModel : ViewModelBase
{
    private readonly TestSession _session;
    private IniFile _settingIni;

    private const string SwitchUnitSection = "SwitchUnitCards";
    private const string ValveSection = "ValveSettings";
    private const string TempSensorSection = "TempSensorSettings";
    private const string DelaySection = "DelaySettings";

    public ObservableCollection<string> ComPorts { get; } = new();
    public ObservableCollection<string> BaudRates { get; } = new() { "9600", "19200", "38400", "57600", "115200" };
    public ObservableCollection<string> DataBits { get; } = new() { "7", "8" };
    public ObservableCollection<string> ParityBits { get; } = new() { "None", "Odd", "Even" };
    public ObservableCollection<string> StopBits { get; } = new() { "1", "1.5", "2" };
    public ObservableCollection<string> GpibAddresses { get; } = new(Enumerable.Range(0, 31).Select(i => i.ToString()));
    public ObservableCollection<string> Ports { get; } = new(Enumerable.Range(0, 8).Select(i => i.ToString()));
    public ObservableCollection<string> CardChannels { get; } = new(Enumerable.Range(301, 16).Select(i => i.ToString()));
    public ObservableCollection<string> ValveChannels { get; } = new(Enumerable.Range(101, 9).Select(i => i.ToString()));
    public ObservableCollection<string> TempChannels { get; } = new(Enumerable.Range(201, 4).Select(i => i.ToString()));
    public ObservableCollection<string> PressureModels { get; } = new();

    private string _daqPort = "COM3";
    public string DaqPort { get => _daqPort; set => SetField(ref _daqPort, value); }
    public string DaqBaud { get; set; } = "9600";
    public string DaqDataBits { get; set; } = "8";
    public string DaqParity { get; set; } = "None";
    public string DaqStopBits { get; set; } = "1";
    private string _ovenPort = "COM6";
    public string OvenPort { get => _ovenPort; set => SetField(ref _ovenPort, value); }
    public string OvenBaud { get; set; } = "19200";
    public string OvenDataBits { get; set; } = "8";
    public string OvenParity { get; set; } = "None";
    public string OvenStopBits { get; set; } = "1";
    public string PressureGpibAddress { get; set; } = "10";
    public string PressureGpibPort { get; set; } = "2";
    public string PressureModelName { get; set; } = "FLUKE-7250";
    public string TempGpibAddress { get; set; } = "9";
    public string TempGpibPort { get; set; } = "0";

    public ObservableCollection<SettingPairVm> SwitchUnitCards { get; } = new();
    public ObservableCollection<SettingPairVm> ValveSettings { get; } = new();
    public ObservableCollection<SettingPairVm> TempSensorSettings { get; } = new();
    public ObservableCollection<SettingPairVm> DelaySettings { get; } = new();
    public ObservableCollection<PressureCommandSettingVm> PressureCommandSettings { get; } = new();

    // ── 设备 ────────────────────────────────────────────────────────────
    public ObservableCollection<DeviceProfile> Devices { get; } = new();

    // ── 工位 ────────────────────────────────────────────────────────────
    public const int SlotMax = SlotLayoutHelper.SlotMax;
    public ObservableCollection<SlotEntry> Slots { get; } = new();
    public int PreviewCount => Slots.Count;

    private int _slotCount = 16;
    public int SlotCount
    {
        get => _slotCount;
        set => SetSlotLayoutField(ref _slotCount, Math.Clamp(value, 1, SlotMax));
    }

    private string _batchNo = $"{DateTime.Now:yyMMdd}_01";
    public string BatchNo { get => _batchNo; set => SetSlotLayoutField(ref _batchNo, value); }

    private int _startIndex = 1;
    public int StartIndex { get => _startIndex; set => SetSlotLayoutField(ref _startIndex, value); }

    private int _startBoard = 1;
    public int StartBoard { get => _startBoard; set => SetSlotLayoutField(ref _startBoard, value); }

    private int _startBoardSlot = 1;
    public int StartBoardSlot { get => _startBoardSlot; set => SetSlotLayoutField(ref _startBoardSlot, value); }

    private int _boardSlotCapacity = 16;
    public int BoardSlotCapacity { get => _boardSlotCapacity; set => SetSlotLayoutField(ref _boardSlotCapacity, Math.Max(1, value)); }

    private int _startValve = 1;
    public int StartValve { get => _startValve; set => SetSlotLayoutField(ref _startValve, value); }

    private int _fixtureSlotCapacity = 8;
    public int FixtureSlotCapacity { get => _fixtureSlotCapacity; set => SetSlotLayoutField(ref _fixtureSlotCapacity, Math.Max(1, value)); }

    private int _fixtureCount = 8;
    public int FixtureCount { get => _fixtureCount; set => SetSlotLayoutField(ref _fixtureCount, Math.Max(1, value)); }

    private int _startChannel = 1;
    public int StartChannel { get => _startChannel; set => SetSlotLayoutField(ref _startChannel, value); }

    private int _startSerial = 1;
    public int StartSerial { get => _startSerial; set => SetSlotLayoutField(ref _startSerial, value); }

    private bool _autoNumber = true;
    public bool AutoNumber { get => _autoNumber; set => SetSlotLayoutField(ref _autoNumber, value); }

    /// <summary>扫码录入需要下一行时扩展，不预生成占位 DEMO 行。</summary>
    public void EnsureSlotCount(int count)
    {
        var target = Math.Clamp(count, 1, SlotMax);
        if (target <= Slots.Count) return;
        SlotCount = target;
    }

    public RelayCommand RegenerateSlotsCommand { get; private set; } = null!;

    // ������ �췽�� ��������������������������������������������������������������������������������������������������������������������������
    private TestPlan _plan = new();
    public TestPlan Plan { get => _plan; private set => SetField(ref _plan, value); }
    public string TaskScript => Plan.TaskScript;
    public ObservableCollection<string> PlanFolders { get; } = new();
    public ObservableCollection<string> SensorModelFiles { get; } = new();
    private string _selectedPlanFolder = "";
    private string _selectedSensorModelFile = "";
    private string _loadedPlanFolder = "";
    private string _loadedSensorModelFile = "";
    private bool _loadingPlan;
    public string SelectedPlanFolder
    {
        get => _selectedPlanFolder;
        set
        {
            if (!SetField(ref _selectedPlanFolder, value)) return;
            RefreshSensorModelFiles();
        }
    }
    public string SelectedSensorModelFile
    {
        get => _selectedSensorModelFile;
        set
        {
            if (!SetField(ref _selectedSensorModelFile, value)) return;
            if (!_loadingPlan && !string.IsNullOrWhiteSpace(value)) LoadPlanByFile(value);
        }
    }
    public ObservableCollection<PressurePoint> PressurePoints { get; } = new();
    public ObservableCollection<TempPoint> TempPoints { get; } = new();

    /// <summary>����Ĭ��ѹ�����͵�������ʾ�������� ComboBox ˫��󶨡�</summary>
    public string PlanDefaultPressureTypeDisplay
    {
        get => Plan.DefaultPressureType switch
        {
            Core.Config.PressureType.Absolute     => "绝压",
            Core.Config.PressureType.Differential => "差压",
            _                                     => "表压",
        };
        set
        {
            Plan.DefaultPressureType = value switch
            {
                "绝压" => Core.Config.PressureType.Absolute,
                "差压" => Core.Config.PressureType.Differential,
                _      => Core.Config.PressureType.Gauge,
            };
            OnPropertyChanged();
        }
    }

    // ������ ��ָ�� ��������������������������������������������������������������������������������������������������������������������������
    public ObservableCollection<ModelCommandsVm> ModelCommands { get; } = new();

    // ── 测试流程 ────────────────────────────────────────────────────────
    public ObservableCollection<TaskStepVm> TaskSteps { get; } = new();

    /// <summary>Catalog of all action keys recognised by <see cref="Core.TaskScript.TaskRunner"/>.</summary>
    public ObservableCollection<string> AvailableActions { get; } = new()
    {
        "Initial:Pressure", "Initial:Temp", "Initial:Board", "Initial:DMM", "Initial:CommuTest",
        "DAQ:ClearData", "DAQ:TestType,测试", "DAQ:Down",
        "TP:SetPressurePoint,1,TEST", "TP:SetPressurePoint,2,TEST", "TP:SetPressurePoint,3,TEST",
        "TP:SetTempPoint,1,TEST", "TP:Vent", "TP:ReturnRoomTemp", "TP:StopTemp",
        "Read:R", "Read:UT", "Read:Usign", "Read:Usource", "Read:Isource", "Read:DMMSample",
        "Save:TestData", "Cal:Test",
    };

    // ── 指标 ────────────────────────────────────────────────────────────
    public ObservableCollection<MetricSwitch> Metrics { get; } = new()
    {
        new() { Code = "Offset",   Name = "零位输出",     Description = "零压输出 / 量程起始点偏差" },
        new() { Code = "Span",     Name = "满量程输出",   Description = "末压输出 - 零压输出" },
        new() { Code = "NL",       Name = "非线性",       Description = "实际曲线与理想直线的偏差" },
        new() { Code = "PH",       Name = "压力迟滞",     Description = "升降同压力点输出差" },
        new() { Code = "TCO",      Name = "TCO",          Description = "零位温度系数" },
        new() { Code = "TCS",      Name = "TCS",          Description = "灵敏度温度系数" },
        new() { Code = "TCR",      Name = "TCR",          Description = "电阻温度系数" },
        new() { Code = "THO",      Name = "温度迟滞",     Description = "升降同温度点输出差" },
        new() { Code = "THS",      Name = "THS",          Description = "灵敏度温度迟滞" },
        new() { Code = "TCT",      Name = "TCT",          Description = "温度传感器温度系数" },
    };

    // ������ ��汾��Ϣ ������������������������������������������������������������������������������������������������������������������
    public string AppVersion => "3.0.0.25 (V2 MVP)";
    public string Changelog { get; }

    // ������ ��ϵͳ���� ������������������������������������������������������������������������������������������������������������������
    public string BaseDir => AppPaths.BaseDir;
    public string LogDir => AppPaths.LogDir;
    public string DataDir => AppPaths.DataDir;
    public string TestConfigDir => AppPaths.TestConfigDir;
    public ObservableCollection<string> Themes { get; } = new() { "深色", "浅色" };

    private string _selectedTheme = "深色";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetField(ref _selectedTheme, value)) return;
            ThemeHelper.Apply(ThemeHelper.FromDisplayName(value));
            AppPreferences.Set(_settingIni, "Theme", ThemeHelper.FromDisplayName(value));
        }
    }

    private int _logRetainDays = 30;
    public int LogRetainDays { get => _logRetainDays; set => SetField(ref _logRetainDays, value); }

    private bool _autoLoadLastPlan = true;
    public bool AutoLoadLastPlan { get => _autoLoadLastPlan; set => SetField(ref _autoLoadLastPlan, value); }

    private bool _autoExportCsv = true;
    public bool AutoExportCsv { get => _autoExportCsv; set => SetField(ref _autoExportCsv, value); }

    private bool _saveCheckpointOnAbort = false;
    public bool SaveCheckpointOnAbort { get => _saveCheckpointOnAbort; set => SetField(ref _saveCheckpointOnAbort, value); }

    private bool _fallbackSimOnDisconnect;
    public bool FallbackSimOnDisconnect { get => _fallbackSimOnDisconnect; set => SetField(ref _fallbackSimOnDisconnect, value); }

    // ── Sub-nav ──────────────────────────────────────────────────────────
    public ObservableCollection<string> Sections { get; } = new()
    {
        "方案", "参数控制", "设备", "指令", "工位", "测试流程", "版本信息", "系统设置",
    };

    private string _selectedSection = "方案";
    public string SelectedSection { get => _selectedSection; set => SetField(ref _selectedSection, value); }

    // ������ Commands ��������������������������������������������������������������������������������������������������������������������
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand AddSlotCommand { get; }
    public RelayCommand BatchGenerateSlotsCommand { get; }
    public RelayCommand ImportSlotsCommand { get; }
    public RelayCommand ExportSlotsCommand { get; }
    public RelayCommand NewPlanFolderCommand { get; }
    public RelayCommand NewSensorModelCommand { get; }
    public RelayCommand BulkEditPressurePointsCommand { get; }
    public RelayCommand BulkEditTempPointsCommand { get; }
    public RelayCommand UsePerformanceFlowCommand { get; }
    public RelayCommand DeletePlanCommand { get; }

    public ConfigViewModel(TestSession session)
    {
        _session = session;
        _settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        Plan = session.Plan;

        LoadPressureModels(session.Commands);

        RefreshComPorts();
        LoadDeviceSettings();
        LoadAppSettings();
        BuildParameterSettings();

        foreach (var d in session.Station.Devices.Values) Devices.Add(d);
        LoadSlotLayoutFromIni();
        foreach (var s in session.Slots.Entries) Slots.Add(s);
        if (Slots.Count > 0)
            _slotCount = Math.Clamp(Slots.Count, 1, SlotMax);
        else
            RegenerateSlots();
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(PreviewCount));
        foreach (var pp in session.Plan.PressurePoints) PressurePoints.Add(pp);
        foreach (var tp in session.Plan.TempPoints) TempPoints.Add(tp);

        BuildModelCommands(session.Commands);
        BuildTaskSteps(session.Plan.TaskScript);
        SyncMetricsFromPlan();
        Changelog = LoadChangelog();
        RefreshPlanFolders();
        AutoSelectCurrentPlan();

        SaveCommand = new RelayCommand(_ => SaveSettings());
        ReloadCommand = new RelayCommand(_ => ReloadSettings());
        AddSlotCommand = new RelayCommand(_ => AddSlot());
        BatchGenerateSlotsCommand = new RelayCommand(_ => BatchGenerateSlots());
        ImportSlotsCommand = new RelayCommand(_ => ImportSlots());
        ExportSlotsCommand = new RelayCommand(_ => ExportSlots());
        RegenerateSlotsCommand = new RelayCommand(_ => ConfirmAndRegenerateSlots());
        NewPlanFolderCommand = new RelayCommand(_ => NewPlanFolder());
        NewSensorModelCommand = new RelayCommand(_ => NewSensorModel());
        BulkEditPressurePointsCommand = new RelayCommand(_ => BulkEditPressurePoints());
        BulkEditTempPointsCommand = new RelayCommand(_ => BulkEditTempPoints());
        UsePerformanceFlowCommand = new RelayCommand(_ => UsePerformanceFlow());
        DeletePlanCommand = new RelayCommand(_ => DeletePlan());
    }

    private void NewPlan()
    {
        var newPlan = new TestPlan
        {
            Name = $"plan_{DateTime.Now:yyyyMMdd_HHmmss}",
            SensorType = "M30-NEW",
            PressureUnit = "kPa",
            Precision = 0.05f,
            TaskScript = "Run:PerformanceTest",
        };
        newPlan.PressurePoints.Add(new PressurePoint("P1", 0));
        newPlan.PressurePoints.Add(new PressurePoint("P2", 50));
        newPlan.PressurePoints.Add(new PressurePoint("P3", 100));
        newPlan.TempPoints.Add(new TempPoint("T1", 25));

        SetPlan(newPlan);


        AppLog.Info("Config", $"已创建新方案 {newPlan.Name}，并写入到 {AppPaths.TestConfigDir}");
    }


    private void NewPlanFolder()
    {
        var folderName = $"Plan_{DateTime.Now:yyyyMMdd_HHmmss}";
        var folderPath = Path.Combine(AppPaths.TestConfigDir, folderName);
        Directory.CreateDirectory(folderPath);
        RefreshPlanFolders();
        _selectedPlanFolder = folderName;
        OnPropertyChanged(nameof(SelectedPlanFolder));
        AppLog.Info("Config", $"已创建新方案文件夹 {folderName}");
    }

    private void NewSensorModel()
    {
        if (string.IsNullOrWhiteSpace(_selectedPlanFolder))
        {
            MessageBox.Show("请先选择一个方案文件夹", "新建传感器型号", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sensorModelName = $"Sensor_{DateTime.Now:yyyyMMdd_HHmmss}";
        var folderPath = Path.Combine(AppPaths.TestConfigDir, _selectedPlanFolder);
        var filePath = Path.Combine(folderPath, sensorModelName + ".ini");

        var newPlan = new TestPlan
        {
            Name = sensorModelName,
            SensorType = "M30-NEW",
            PressureUnit = "kPa",
            Precision = 0.05f,
            TaskScript = "Run:PerformanceTest",
        };
        newPlan.PressurePoints.Add(new PressurePoint("P1", 0));
        newPlan.PressurePoints.Add(new PressurePoint("P2", 50));
        newPlan.PressurePoints.Add(new PressurePoint("P3", 100));
        newPlan.TempPoints.Add(new TempPoint("T1", 25));

        newPlan.Save(filePath);
        RefreshSensorModelFiles();
        _selectedSensorModelFile = sensorModelName;
        OnPropertyChanged(nameof(SelectedSensorModelFile));
        SetPlan(newPlan);
        AppLog.Info("Config", $"已创建新传感器型号 {sensorModelName} 在方案 {_selectedPlanFolder}");
    }
    private void DeletePlan()
    {
        var name = Plan.Name;
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(AppPaths.TestConfigDir, name + ".ini");
        if (!File.Exists(path))
        {
            MessageBox.Show($"方案文件不存在：{path}", "删除方案", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var result = MessageBox.Show($"确定要删除方案「{name}」吗？\n此操作不可撤销。", "删除方案",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(path);
    
            if (PlanFolders.Count > 0)
            {
                LoadPlanByFile(SensorModelFiles[0]);
            }
            else
            {
                NewPlan();
            }
            AppLog.Info("Config", $"已删除方案 {name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除方案", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UsePerformanceFlow()
    {
        Plan.TaskScript = "Run:PerformanceTest";
        OnPropertyChanged(nameof(TaskScript));
        BuildTaskSteps(Plan.TaskScript);
        AppLog.Info("Config", "已切换为完整性能测试流程 Run:PerformanceTest");
    }

    private void BulkEditPressurePoints()
    {
        var text = string.Join(Environment.NewLine, PressurePoints.Select(p => $"{p.Name},{p.Value.ToString(CultureInfo.InvariantCulture)}"));
        var dlg = new Views.BulkPointEditorWindow("批量录入压力点", text) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var points = ParsePressurePoints(dlg.Text).ToList();
            PressurePoints.Clear();
            foreach (var p in points) PressurePoints.Add(p);
            AppLog.Info("Config", $"批量录入压力点 {points.Count} 个");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "压力点录入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BulkEditTempPoints()
    {
        var text = string.Join(Environment.NewLine, TempPoints.Select(t =>
            string.IsNullOrWhiteSpace(t.SoakMinutesText)
                ? $"{t.Name},{t.Celsius.ToString(CultureInfo.InvariantCulture)}"
                : $"{t.Name},{t.Celsius.ToString(CultureInfo.InvariantCulture)},{t.SoakMinutesText}"));
        var dlg = new Views.BulkPointEditorWindow("批量录入温度点", text) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var points = ParseTempPoints(dlg.Text).ToList();
            TempPoints.Clear();
            foreach (var p in points) TempPoints.Add(p);
            AppLog.Info("Config", $"批量录入温度点 {points.Count} 个");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "温度点录入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static IEnumerable<PressurePoint> ParsePressurePoints(string text)
    {
        var index = 1;
        foreach (var line in SplitPointLines(text))
        {
            var parts = SplitPointParts(line);
            if (parts.Length == 1)
                yield return new PressurePoint($"P{index}", ParseFloat(parts[0], line));
            else
                yield return new PressurePoint(parts[0], ParseFloat(parts[1], line));
            index++;
        }
    }

    private static IEnumerable<TempPoint> ParseTempPoints(string text)
    {
        var index = 1;
        foreach (var line in SplitPointLines(text))
        {
            var parts = SplitPointParts(line);
            if (parts.Length == 1)
            {
                yield return new TempPoint($"T{index}", ParseFloat(parts[0], line));
            }
            else
            {
                int? soak = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
                yield return new TempPoint(parts[0], ParseFloat(parts[1], line), soak);
            }
            index++;
        }
    }

    private static IEnumerable<string> SplitPointLines(string text) =>
        text.Split(new[] { "\r\n", "\n", "\r", ";" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l));

    private static string[] SplitPointParts(string line) =>
        line.Split(new[] { ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static float ParseFloat(string value, string line)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        throw new FormatException($"�޷�������ֵ��{line}");
    }

    private void RefreshPlanFolders()
    {
        PlanFolders.Clear();
        if (!Directory.Exists(AppPaths.TestConfigDir)) return;

        foreach (var dir in Directory.GetDirectories(AppPaths.TestConfigDir))
            PlanFolders.Add(Path.GetFileName(dir));
    }

    /// <summary>启动时自动选中当前方案所在的文件夹和传感器型号。</summary>
    private void AutoSelectCurrentPlan()
    {
        if (PlanFolders.Count == 0) return;

        // 找到包含当前方案的文件夹
        var planName = Plan.Name;
        string? matchFolder = null;
        foreach (var folder in PlanFolders)
        {
            var path = Path.Combine(AppPaths.TestConfigDir, folder, planName + ".ini");
            if (File.Exists(path)) { matchFolder = folder; break; }
        }
        // 没找到就选第一个文件夹
        matchFolder ??= PlanFolders[0];

        _loadingPlan = true;
        _selectedPlanFolder = matchFolder;
        _loadedPlanFolder = matchFolder;
        OnPropertyChanged(nameof(SelectedPlanFolder));
        RefreshSensorModelFiles();

        // 选中型号
        if (SensorModelFiles.Contains(planName))
            _selectedSensorModelFile = planName;
        else if (SensorModelFiles.Count > 0)
            _selectedSensorModelFile = SensorModelFiles[0];
        _loadedSensorModelFile = _selectedSensorModelFile;
        OnPropertyChanged(nameof(SelectedSensorModelFile));
        _loadingPlan = false;
    }

    private void RefreshSensorModelFiles()
    {
        SensorModelFiles.Clear();
        if (string.IsNullOrWhiteSpace(_selectedPlanFolder)) return;

        var folderPath = Path.Combine(AppPaths.TestConfigDir, _selectedPlanFolder);
        if (Directory.Exists(folderPath))
        {
            foreach (var file in Directory.GetFiles(folderPath, "*.ini"))
                SensorModelFiles.Add(Path.GetFileNameWithoutExtension(file));
        }
    }

    private void LoadPlanByFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_selectedPlanFolder)) return;
        if (string.Equals(Plan.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;

        var filePath = Path.Combine(AppPaths.TestConfigDir, _selectedPlanFolder, fileName + ".ini");
        if (!File.Exists(filePath)) return;

        var plan = TestPlan.Load(filePath);
        SetPlan(plan);
        _loadedPlanFolder = _selectedPlanFolder;
        _loadedSensorModelFile = fileName;
        AppLog.Info("Config", $"已切换到传感器型号 {fileName}");
    }

    private void SetPlan(TestPlan plan)
    {
        _loadingPlan = true;
        Plan = plan;
        _session.Plan = plan;
        _session.Context.Plan = plan;
        _selectedSensorModelFile = plan.Name;
        OnPropertyChanged(nameof(TaskScript));
        OnPropertyChanged(nameof(SelectedSensorModelFile));
        OnPropertyChanged(nameof(PlanDefaultPressureTypeDisplay));
        PressurePoints.Clear();
        foreach (var pp in plan.PressurePoints) PressurePoints.Add(pp);
        TempPoints.Clear();
        foreach (var tp in plan.TempPoints) TempPoints.Add(tp);
        SyncMetricsFromPlan();
        _loadingPlan = false;
    }

    private void SavePlan()
    {
        Plan.PressurePoints.Clear();
        foreach (var pp in PressurePoints) Plan.PressurePoints.Add(pp);
        Plan.TempPoints.Clear();
        foreach (var tp in TempPoints) Plan.TempPoints.Add(tp);
        SaveMetricsToPlan();

        var name = CleanPathName(string.IsNullOrWhiteSpace(Plan.Name) ? "plan" : Plan.Name);
        Plan.Name = name;
        Plan.SensorType = name;

        var folderName = CleanPathName(string.IsNullOrWhiteSpace(_selectedPlanFolder) ? "M30测试" : _selectedPlanFolder);
        var oldFolder = string.IsNullOrWhiteSpace(_loadedPlanFolder) ? folderName : _loadedPlanFolder;
        var oldFolderPath = Path.Combine(AppPaths.TestConfigDir, oldFolder);
        var folder = Path.Combine(AppPaths.TestConfigDir, folderName);
        if (!string.Equals(oldFolder, folderName, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(oldFolderPath) &&
            !Directory.Exists(folder))
        {
            Directory.Move(oldFolderPath, folder);
        }
        Directory.CreateDirectory(folder);
        var oldName = string.IsNullOrWhiteSpace(_loadedSensorModelFile) ? name : _loadedSensorModelFile;
        var oldPath = Path.Combine(folder, oldName + ".ini");
        var path = Path.Combine(folder, name + ".ini");
        if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(oldPath) &&
            !File.Exists(path))
        {
            File.Move(oldPath, path);
        }
        Plan.Save(path);
        RefreshPlanFolders();
        _selectedPlanFolder = folderName;
        _loadedPlanFolder = folderName;
        OnPropertyChanged(nameof(SelectedPlanFolder));
        RefreshSensorModelFiles();
        _selectedSensorModelFile = name;
        _loadedSensorModelFile = name;
        OnPropertyChanged(nameof(SelectedSensorModelFile));

        AppLog.Info("Config", $"已保存方案 {name} 到 {path}");
    }

    private void AddSlot()
    {
        if (Slots.Count >= SlotMax) return;
        SlotCount = Slots.Count + 1;
    }

    private static string CleanPathName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "plan" : clean;
    }

    private void BatchGenerateSlots()
    {
        var dlg = new Views.SlotCountDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Count = Math.Max(Slots.Count, 1)
        };
        if (dlg.ShowDialog() != true) return;
        SlotCount = Math.Clamp(dlg.Count, 1, SlotMax);
    }

    private void ImportSlots()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CSV 文件|*.csv",
            InitialDirectory = System.IO.Path.GetDirectoryName(AppPaths.SlotCsv)
        };
        if (dlg.ShowDialog() != true) return;
        var table = SlotTable.Load(dlg.FileName);
        Slots.Clear();
        foreach (var s in table.Entries) Slots.Add(s);
        _slotCount = Math.Clamp(Slots.Count, 1, SlotMax);
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(PreviewCount));
        AppLog.Info("Config", $"已从 {dlg.FileName} 导入 {Slots.Count} 行工位");
    }

    private void ExportSlots()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            InitialDirectory = System.IO.Path.GetDirectoryName(AppPaths.SlotCsv),
            FileName = "工位对应表.csv"
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("工位,序列号,阀位,板卡位,板卡工位号,层数,夹具位,夹具工位号,压力控制器,数字万用表,通道,阀门");
        foreach (var s in SlotLayoutHelper.TrimTrailingPlaceholders(Slots.ToList()))
            sb.AppendLine(string.Join(',', s.Slot, s.SerialNo, s.Valve, s.Board, s.BoardSlotNo,
                s.Layer, s.Fixture, s.FixtureSlotNo, s.PressureController, s.Dmm, s.Channel, s.ValveAddr));
        File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
        AppLog.Info("Config", $"已导出 {Slots.Count} 行工位到 {dlg.FileName}");
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

    private bool SetSlotLayoutField<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var changed = SetField(ref storage, value, name);
        if (changed)
            RegenerateSlots();
        return changed;
    }

    private void ConfirmAndRegenerateSlots()
    {
        var result = System.Windows.MessageBox.Show(
            "刷新将根据当前参数重新生成工位表，已手动修改的序列号会尽量保留。\n\n确定要刷新吗？",
            "确认刷新",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result == System.Windows.MessageBoxResult.Yes)
            RegenerateSlots();
    }

    public void RegenerateSlots()
    {
        var preserved = SlotLayoutHelper.CollectSerialMap(Slots);
        var generated = SlotLayoutHelper.Generate(BuildSlotLayoutOptions());
        SlotLayoutHelper.ApplyPreservedSerials(generated, preserved);

        Slots.Clear();
        foreach (var s in generated) Slots.Add(s);
        OnPropertyChanged(nameof(PreviewCount));
    }

    private void LoadSlotLayoutFromIni()
    {
        if (int.TryParse(_settingIni.Get("Slots", "Count", ""), out var savedCount) && savedCount > 0)
            _slotCount = Math.Clamp(savedCount, 1, SlotMax);
        var savedBatch = _settingIni.Get("Slots", "BatchNo", "");
        if (!string.IsNullOrWhiteSpace(savedBatch)) _batchNo = savedBatch;
        if (int.TryParse(_settingIni.Get("Slots", "StartIndex", ""), out var si)) _startIndex = si;
        if (int.TryParse(_settingIni.Get("Slots", "StartBoard", ""), out var sb)) _startBoard = sb;
        if (int.TryParse(_settingIni.Get("Slots", "StartBoardSlot", ""), out var sbs)) _startBoardSlot = sbs;
        if (int.TryParse(_settingIni.Get("Slots", "BoardSlotCapacity", ""), out var bsc) && bsc > 0) _boardSlotCapacity = bsc;
        if (int.TryParse(_settingIni.Get("Slots", "StartValve", ""), out var sv)) _startValve = sv;
        if (int.TryParse(_settingIni.Get("Slots", "FixtureSlotCapacity", ""), out var fsc) && fsc > 0) _fixtureSlotCapacity = fsc;
        if (int.TryParse(_settingIni.Get("Slots", "FixtureCount", ""), out var fc) && fc > 0) _fixtureCount = fc;
        if (int.TryParse(_settingIni.Get("Slots", "StartChannel", ""), out var sc)) _startChannel = sc;
        if (int.TryParse(_settingIni.Get("Slots", "StartSerial", ""), out var ss)) _startSerial = ss;
        if (bool.TryParse(_settingIni.Get("Slots", "AutoNumber", ""), out var an)) _autoNumber = an;
    }

    private void SaveSlotLayoutToIni()
    {
        _settingIni.Set("Slots", "Count", _slotCount.ToString());
        _settingIni.Set("Slots", "BatchNo", _batchNo);
        _settingIni.Set("Slots", "StartIndex", _startIndex.ToString());
        _settingIni.Set("Slots", "StartBoard", _startBoard.ToString());
        _settingIni.Set("Slots", "StartBoardSlot", _startBoardSlot.ToString());
        _settingIni.Set("Slots", "BoardSlotCapacity", _boardSlotCapacity.ToString());
        _settingIni.Set("Slots", "StartValve", _startValve.ToString());
        _settingIni.Set("Slots", "FixtureSlotCapacity", _fixtureSlotCapacity.ToString());
        _settingIni.Set("Slots", "FixtureCount", _fixtureCount.ToString());
        _settingIni.Set("Slots", "StartChannel", _startChannel.ToString());
        _settingIni.Set("Slots", "StartSerial", _startSerial.ToString());
        _settingIni.Set("Slots", "AutoNumber", _autoNumber.ToString());
        _settingIni.Set("Slots", "LastPlan", Plan.Name);
    }

    private void LoadDeviceSettings()
    {
        var dac = _session.Station.Get(DeviceKind.Dac);
        DaqPort = _settingIni.Get("Device.Dac", "Address", dac?.Address ?? DaqPort);
        DaqBaud = _settingIni.Get("Device.Dac", "Baud", (dac?.Baud ?? 9600).ToString());
        DaqDataBits = _settingIni.Get("Device.Dac", "DataBits", (dac?.DataBits ?? 8).ToString());
        DaqParity = NormalizeParity(_settingIni.Get("Device.Dac", "Parity", dac?.Parity ?? "None"));
        DaqStopBits = _settingIni.Get("Device.Dac", "StopBits", dac?.StopBits ?? "1");

        var oven = _session.Station.Get(DeviceKind.Oven);
        OvenPort = _settingIni.Get("Device.Oven", "Address", oven?.Address ?? OvenPort);
        OvenBaud = _settingIni.Get("Device.Oven", "Baud", (oven?.Baud ?? 19200).ToString());
        OvenDataBits = _settingIni.Get("Device.Oven", "DataBits", (oven?.DataBits ?? 8).ToString());
        OvenParity = NormalizeParity(_settingIni.Get("Device.Oven", "Parity", oven?.Parity ?? "None"));
        OvenStopBits = _settingIni.Get("Device.Oven", "StopBits", oven?.StopBits ?? "1");

        var pressure = _session.Station.Get(DeviceKind.Pressure);
        PressureModelName = _settingIni.Get("Device.Pressure", "Model", pressure?.Model ?? PressureModelName);
        ParseGpibAddress(_settingIni.Get("Device.Pressure", "Address", pressure?.Address ?? ""), out var pressurePort, out var pressureAddress);
        PressureGpibPort = pressurePort;
        PressureGpibAddress = pressureAddress;

        var dmm = _session.Station.Get(DeviceKind.Dmm);
        ParseGpibAddress(_settingIni.Get("Device.Dmm", "Address", dmm?.Address ?? ""), out var tempPort, out var tempAddress);
        TempGpibPort = tempPort;
        TempGpibAddress = tempAddress;
    }

    private void RefreshComPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ComPorts.Clear();
        foreach (var port in ports) ComPorts.Add(port);
        if (ComPorts.Count == 0) ComPorts.Add("(�޿��ô�??");
        if (!ComPorts.Contains(DaqPort)) DaqPort = ComPorts[0];
        if (!ComPorts.Contains(OvenPort)) OvenPort = ComPorts.Count > 1 ? ComPorts[1] : ComPorts[0];
    }

    private void BuildParameterSettings()
    {
        SwitchUnitCards.Clear();
        ValveSettings.Clear();
        TempSensorSettings.Clear();
        DelaySettings.Clear();
        PressureCommandSettings.Clear();

        for (var i = 1; i <= 16; i++)
            SwitchUnitCards.Add(new SettingPairVm($"卡{i}", LoadSetting(SwitchUnitSection, $"Card{i}", (300 + i).ToString()), CardChannels, section: SwitchUnitSection, key: $"Card{i}"));

        ValveSettings.Add(new SettingPairVm("总阀", LoadSetting(ValveSection, "MasterValve", "101"), ValveChannels, section: ValveSection, key: "MasterValve"));
        for (var i = 1; i <= 8; i++)
            ValveSettings.Add(new SettingPairVm($"阀门{i}", LoadSetting(ValveSection, $"Valve{i}", (101 + i).ToString()), ValveChannels, section: ValveSection, key: $"Valve{i}"));

        for (var i = 1; i <= 4; i++)
            TempSensorSettings.Add(new SettingPairVm($"温度传感器{i}", LoadSetting(TempSensorSection, $"Sensor{i}", (200 + i).ToString()), TempChannels, section: TempSensorSection, key: $"Sensor{i}"));

        DelaySettings.Add(new SettingPairVm("加压后等待时间", LoadSetting(DelaySection, "PressureAfterMs", "60000"), null, "毫秒(ms)", DelaySection, "PressureAfterMs"));
        DelaySettings.Add(new SettingPairVm("探漏等待时间", LoadSetting(DelaySection, "LeakWaitMs", "500"), null, "毫秒(ms)", DelaySection, "LeakWaitMs"));
        DelaySettings.Add(new SettingPairVm("泄压等待时间", LoadSetting(DelaySection, "VentWaitMs", "120000"), null, "毫秒(ms)", DelaySection, "VentWaitMs"));
        DelaySettings.Add(new SettingPairVm("Usig采集延迟", LoadSetting(DelaySection, "UsigDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UsigDelayMs"));
        DelaySettings.Add(new SettingPairVm("UT采集延迟", LoadSetting(DelaySection, "UtDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UtDelayMs"));
        DelaySettings.Add(new SettingPairVm("Usource采集延迟", LoadSetting(DelaySection, "UsourceDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UsourceDelayMs"));
        DelaySettings.Add(new SettingPairVm("Isource采集延迟", LoadSetting(DelaySection, "IsourceDelayMs", "300"), null, "毫秒(ms)", DelaySection, "IsourceDelayMs"));
        DelaySettings.Add(new SettingPairVm("继电器阀门等待时间", LoadSetting(DelaySection, "ValveSwitchMs", "500"), null, "毫秒(ms)", DelaySection, "ValveSwitchMs"));
        DelaySettings.Add(new SettingPairVm("设定温度等待时间", LoadSetting(DelaySection, "SetTempMs", "10000"), null, "毫秒(ms)", DelaySection, "SetTempMs"));
        DelaySettings.Add(new SettingPairVm("保温时间", LoadSetting(DelaySection, "SoakMinutes", "120"), null, "分钟(min)", DelaySection, "SoakMinutes"));
        DelaySettings.Add(new SettingPairVm("零点校验等待时间", LoadSetting(DelaySection, "ZeroCheckMs", "0"), null, "毫秒(ms)", DelaySection, "ZeroCheckMs"));
        PressureCommandSettings.Add(new("判断型号", "7250"));
        PressureCommandSettings.Add(new("Open发送指令", "*RST;*IDN?"));
        PressureCommandSettings.Add(new("MachineType发送指令", "*IDN?"));
        PressureCommandSettings.Add(new("UpperLimit发送指令", "CALC:LIM:UPP?"));
        PressureCommandSettings.Add(new("SetPressure发送指令", "*CLS;UNIT {0};PRES {1};TOL {2};OUTP:MC"));
        PressureCommandSettings.Add(new("Vent发送指令", "*CLS;OUTP:MODE VENT"));
        PressureCommandSettings.Add(new("ZeroCheck发送指令", "*CLS;CAL:ZERO:INIT;CAL:ZERO:RUN"));
        PressureCommandSettings.Add(new("ReadPressure发送指令", "*CLS;MEAS?"));
        PressureCommandSettings.Add(new("SetMeasure发送指令", "*CLS;OUTP:MODE MEAS"));
        PressureCommandSettings.Add(new("SelfTest发送指令", "*TST?"));
        PressureCommandSettings.Add(new("ReadStatus发送指令", "*CLS;STAT:OPER:COND?"));
        PressureCommandSettings.Add(new("SetAbs发送指令", "*CLS;SENSE:MODE ABS"));
        PressureCommandSettings.Add(new("SetGaug发送指令", "*CLS;SENSE:MODE GAUG"));
        PressureCommandSettings.Add(new("SetDiff发送指令", "*CLS;SENSE:MODE DIFF"));
    }

    private string LoadSetting(string section, string key, string fallback) =>
        _settingIni.Get(section, key, fallback);

    private void SaveSettings()
    {
        SaveDeviceProfiles();
        SaveAppSettings();
        SavePairs(SwitchUnitCards);
        SavePairs(ValveSettings);
        SavePairs(TempSensorSettings);
        SavePairs(DelaySettings);
        _settingIni.Save(AppPaths.SettingIni);
        _session.Context.Settings = _settingIni;
        ApplyDeviceProfilesToSession();
        SavePlan();
        SaveSlotLayoutToIni();
        SaveSlotsToDefaultCsv();
        AppPreferences.PruneOldLogs(_settingIni);
        AppLog.Info("Config", $"已保存所有配置到 {AppPaths.SettingIni}");
    }

    private void ApplyDeviceProfilesToSession()
    {
        var station = StationProfile.Load(_settingIni);
        foreach (var kv in station.Devices)
            _session.Station.Devices[kv.Key] = kv.Value;
        _session.RebuildDevices(AppPreferences.DebugMode(_settingIni));
    }

    private void ReloadSettings()
    {
        _settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        LoadDeviceSettings();
        LoadAppSettings();
        BuildParameterSettings();
        AppLog.Info("Config", $"已从 {AppPaths.SettingIni} 重载参数设置");
    }

    private void LoadAppSettings()
    {
        _selectedTheme = ThemeHelper.ToDisplayName(AppPreferences.Theme(_settingIni));
        OnPropertyChanged(nameof(SelectedTheme));
        LogRetainDays = AppPreferences.LogRetainDays(_settingIni);
        AutoLoadLastPlan = AppPreferences.AutoLoadLastPlan(_settingIni);
        AutoExportCsv = AppPreferences.AutoExportCsv(_settingIni);
        SaveCheckpointOnAbort = AppPreferences.SaveCheckpointOnAbort(_settingIni);
        FallbackSimOnDisconnect = AppPreferences.FallbackSimOnDisconnect(_settingIni);
    }

    private void SaveAppSettings()
    {
        AppPreferences.Set(_settingIni, "Theme", ThemeHelper.FromDisplayName(SelectedTheme));
        AppPreferences.Set(_settingIni, "LogRetainDays", LogRetainDays.ToString(CultureInfo.InvariantCulture));
        AppPreferences.SetBool(_settingIni, "AutoLoadLastPlan", AutoLoadLastPlan);
        AppPreferences.SetBool(_settingIni, "AutoExportCsv", AutoExportCsv);
        AppPreferences.SetBool(_settingIni, "SaveCheckpointOnAbort", SaveCheckpointOnAbort);
        AppPreferences.SetBool(_settingIni, "FallbackSimOnDisconnect", FallbackSimOnDisconnect);
        AppPreferences.Set(_settingIni, "LastPlan", Plan.Name);
    }

    private void SyncMetricsFromPlan()
    {
        foreach (var m in Metrics)
        {
            m.Enabled = Plan.IsMetricEnabled(m.Code);
            m.BindSpec(Plan.Specs[m.Code]);
        }
    }

    private void SaveMetricsToPlan()
    {
        Plan.EnabledMetrics.Clear();
        foreach (var m in Metrics)
            Plan.EnabledMetrics[m.Code] = m.Enabled;
    }

    private void SaveSlotsToDefaultCsv()
    {
        try
        {
            var list = SlotLayoutHelper.TrimTrailingPlaceholders(Slots.ToList());
            var table = new SlotTable(list);
            table.Save(AppPaths.SlotCsv);
            _session.ApplyRunConfig(Plan, table);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Config", $"保存工位表失败: {ex.Message}");
        }
    }

    private void SavePairs(IEnumerable<SettingPairVm> settings)
    {
        foreach (var setting in settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Section) || string.IsNullOrWhiteSpace(setting.Key)) continue;
            _settingIni.Set(setting.Section, setting.Key, setting.Value);
        }
    }

    private void SaveDeviceProfiles()
    {
        _settingIni.Set("Device.Dac", "Address", DaqPort);
        _settingIni.Set("Device.Dac", "Baud", DaqBaud);
        _settingIni.Set("Device.Dac", "DataBits", DaqDataBits);
        _settingIni.Set("Device.Dac", "Parity", DaqParity);
        _settingIni.Set("Device.Dac", "StopBits", DaqStopBits);

        _settingIni.Set("Device.Oven", "Address", OvenPort);
        _settingIni.Set("Device.Oven", "Baud", OvenBaud);
        _settingIni.Set("Device.Oven", "DataBits", OvenDataBits);
        _settingIni.Set("Device.Oven", "Parity", OvenParity);
        _settingIni.Set("Device.Oven", "StopBits", OvenStopBits);

        _settingIni.Set("Device.Pressure", "Model", PressureModelName);
        _settingIni.Set("Device.Pressure", "Address", BuildGpibAddress(PressureGpibPort, PressureGpibAddress));
        _settingIni.Set("Device.Dmm", "Address", BuildGpibAddress(TempGpibPort, TempGpibAddress));
    }

    private static string NormalizeParity(string value) =>
        value.Equals("N", StringComparison.OrdinalIgnoreCase) ? "None" : value;

    private static void ParseGpibAddress(string resource, out string port, out string address)
    {
        port = "0";
        address = "0";
        if (string.IsNullOrWhiteSpace(resource)) return;
        if (!resource.StartsWith("GPIB", StringComparison.OrdinalIgnoreCase)) return;

        var parts = resource.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        port = parts[0].Substring(4);
        address = parts[1];
    }

    private static string BuildGpibAddress(string port, string address) => $"GPIB{port}::{address}::INSTR";

    private void BuildModelCommands(CommandDictionary commands)
    {
        // Surface a representative slice of Command.ini per device kind.
        var pressureModels = commands.Models
            .Where(m => commands.Has(m, "SetPressure") || commands.Has(m, "ReadPressure"))
            .OrderBy(m => m)
            .ToArray();
        var slice = new (string Kind, string[] Models, string[] Actions)[]
        {
            ("压力控制器", new[] { "FLUKE-7250", "FLUKE-6270", "WIKA-CPC8000" },
                new[] { "Open", "MachineType", "UpperLimit", "SetPressure", "Vent", "SetAbs",
                        "ZeroCheck", "ReadPressure", "SetMeasure", "SelfTest", "ReadStatus", "SetGaug", "SetDiff" }),
            ("烘箱",      new[] { "GWSEBWT1670", "GWNMC2000" },
                new[] { "Open", "Set", "Read", "Stop", "SelfTest" }),
            ("数字万用表", new[] { "Keysight-34970A", "Keysight-DAQ973A" },
                new[] { "Open", "Close", "SetVol", "SetRes", "ReadValue", "SelfTest" }),
            ("采集卡",    new[] { "M30-DAC" },
                new[] { "Open", "Usig", "Usource", "Isource", "UT", "SelfTest" }),
            ("通道/板卡",  new[] { "Board" },
                new[] { "Open", "Close", "SelfTest" }),
            ("电源",      new[] { "ADCMT-6146" },
                new[] { "Open", "VoltageSource", "CurrentSource", "OutputON", "OutputOFF", "SelfTest" }),
        };

        foreach (var grp in slice)
        foreach (var model in grp.Models)
        {
            var vm = new ModelCommandsVm { Kind = grp.Kind, Model = model };
            foreach (var action in grp.Actions)
            {
                var tpl = commands.Render(model, action) is { Length: > 0 } t ? t : "";
                vm.Templates.Add(new CommandTemplateVm { Action = action, Template = tpl });
            }
            ModelCommands.Add(vm);
        }

        foreach (var model in pressureModels.Where(m => !ModelCommands.Any(x => x.Model.Equals(m, StringComparison.OrdinalIgnoreCase))))
        {
            var vm = new ModelCommandsVm { Kind = "压力控制器", Model = model };
            foreach (var action in new[] { "Open", "MachineType", "UpperLimit", "SetPressure", "Vent", "SetAbs",
                         "ZeroCheck", "ReadPressure", "SetMeasure", "SelfTest", "ReadStatus", "SetGaug", "SetDiff" })
            {
                var tpl = commands.Render(model, action) is { Length: > 0 } t ? t : "";
                vm.Templates.Add(new CommandTemplateVm { Action = action, Template = tpl });
            }
            ModelCommands.Add(vm);
        }
    }

    private void LoadPressureModels(CommandDictionary commands)
    {
        PressureModels.Clear();
        foreach (var model in commands.Models
                     .Where(m => commands.Has(m, "SetPressure") || commands.Has(m, "ReadPressure"))
                     .OrderBy(m => m))
        {
            PressureModels.Add(model);
        }

        if (PressureModels.Count == 0)
        {
            PressureModels.Add("FLUKE-7250");
            PressureModels.Add("FLUKE-6270");
            PressureModels.Add("WIKA-CPC8000");
        }
    }

    private void BuildTaskSteps(string script)
    {
        TaskSteps.Clear();
        var parts = (script ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            TaskSteps.Add(new TaskStepVm { Index = i, Text = parts[i].Trim() });
    }

    private string LoadChangelog()
    {
        // Prefer real CHANGELOG.md if present; otherwise show the V2 dev notes inline.
        var p = Path.Combine(AppPaths.BaseDir, "..", "..", "..", "..", "..", "CHANGELOG.md");
        try { if (File.Exists(p)) return File.ReadAllText(p); } catch { /* fall back */ }

        return """
        # M30TestApp V2 ���
        
        ## 3.0.0.25 - 2026-05-25
        ### ����
        - WPF ������� + ��ɫ���� + ��ർ��
        - 256 ��λ���ޣ�SIM �Զ����� + ??�����⻯
        - �ֶ����� TX/RX ���� + �����Ͳ�??(Usig/UT/Usource/Isource/DMM_V/DMM_R)
        - �������� 8 ��ģ��ռ??        ### �޸�
        - AsyncRelayCommand �쳣���� ??���� + ��־����������
        - �������� DMM-V ���� Binding ���� ??DataMatrix.SanitizeKey
        
        ## 3.0.0.0 - 2026-05-20
        ### �ع�
        - ??WinForms ASLab ������Ժ��ģ���дΪ MVVM
        - TaskScript ���������Ӳ����????��λ����ѭ��
        - DataMatrix �¼���ȡ??DataGridView ֱ��
        - �豸�ӿ�??+ SIM/HW ����
        """;
    }
}




