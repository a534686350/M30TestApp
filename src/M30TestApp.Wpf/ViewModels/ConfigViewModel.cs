using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;
using M30TestApp.Wpf.Themes;

namespace M30TestApp.Wpf.ViewModels;

// 配置中心主 ViewModel。物理拆分为多个 partial 文件（同目录）：
//   ConfigViewModel.Slots.cs  —— 工位布局
//   ConfigViewModel.Plan.cs   —— 方案/压力温度点/探漏
// 小型行 VM 见 ConfigSupportViewModels.cs。
public sealed partial class ConfigViewModel : ViewModelBase
{
    private readonly TestSession _session;
    private readonly CommandDictionary _commands;
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
    public ObservableCollection<string> DmmModels { get; } = new() { "Keysight-DAQ970A", "Keysight-DAQ973A", "Keysight-34970A" };
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
    private string _pressureModelName = "FLUKE-7250";
    public string PressureModelName
    {
        get => _pressureModelName;
        set
        {
            if (SetField(ref _pressureModelName, value))
                LoadPressureCommandSettings();
        }
    }
    public string TempGpibAddress { get; set; } = "9";
    public string TempGpibPort { get; set; } = "0";
    public string DmmModelName { get; set; } = "Keysight-DAQ973A";

    public ObservableCollection<SettingPairVm> SwitchUnitCards { get; } = new();
    public ObservableCollection<SettingPairVm> ValveSettings { get; } = new();
    public ObservableCollection<SettingPairVm> TempSensorSettings { get; } = new();
    public ObservableCollection<SettingPairVm> DelaySettings { get; } = new();
    public ObservableCollection<PressureCommandSettingVm> PressureCommandSettings { get; } = new();

    // ── 设备 ────────────────────────────────────────────────────────────
    public ObservableCollection<DeviceProfile> Devices { get; } = new();


    // ������ ��ָ�� ��������������������������������������������������������������������������������������������������������������������������
    public ObservableCollection<ModelCommandsVm> ModelCommands { get; } = new();
    public ObservableCollection<string> CommandDeviceKinds { get; } = new();
    public ObservableCollection<ModelCommandsVm> CommandModels { get; } = new();

    private string _selectedCommandDeviceKind = "";
    public string SelectedCommandDeviceKind
    {
        get => _selectedCommandDeviceKind;
        set
        {
            if (!SetField(ref _selectedCommandDeviceKind, value)) return;
            RefreshCommandModels();
        }
    }

    private ModelCommandsVm? _selectedModelCommand;
    public ModelCommandsVm? SelectedModelCommand
    {
        get => _selectedModelCommand;
        set => SetField(ref _selectedModelCommand, value);
    }

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
    public string AppVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "1.2.20" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
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
    public RelayCommand DeletePressurePointCommand { get; }
    public RelayCommand DeleteTempPointCommand { get; }
    public RelayCommand UsePerformanceFlowCommand { get; }
    public RelayCommand DeletePlanCommand { get; }

    public ConfigViewModel(TestSession session)
    {
        _session = session;
        _commands = session.Commands;
        _settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        Plan = session.Plan;

        LoadPressureModels(session.Commands);

        RefreshComPorts();
        LoadDeviceSettings();
        LoadAppSettings();
        BuildParameterSettings();
        LoadPressureCommandSettings();

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
        DeletePressurePointCommand = new RelayCommand(p =>
        {
            if (p is PressurePoint pp) PressurePoints.Remove(pp);
        });
        DeleteTempPointCommand = new RelayCommand(p =>
        {
            if (p is TempPoint tp) TempPoints.Remove(tp);
        });
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
        var rows = PressurePoints
            .Select((p, index) => new Views.PointBatchRuleInput(
                PointBatchParser.IndexFromName(p.Name, "P", index + 1),
                p.Value.ToString(CultureInfo.InvariantCulture),
                PointBatchParser.PressureTypeToDisplay(p.PressureType)))
            .ToList();
        if (rows.Count == 0)
            rows.Add(new Views.PointBatchRuleInput("1 20", "0", PlanDefaultPressureTypeDisplay));

        var hint = "压力录入：范围/序号支持「1 20」（从第 1 个起共 20 点）、「20」（从 P1 起共 20 点）、「1-20」；压力值填如 0、100；压力类型可填表压、绝压、差压，留空则用默认。例：1 20 + 0 → P1–P20 均为 0。";
        var dlg = new Views.PointBatchEditorWindow("批量录入压力点", "压力值", "压力类型", hint, rows)
        {
            Owner = Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var points = Core.Config.PointBatchParser.BuildPressurePoints(
                ToRows(dlg.ResultRules), Plan.DefaultPressureType).ToList();
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
        var rows = TempPoints
            .Select((t, index) => new Views.PointBatchRuleInput(
                PointBatchParser.IndexFromName(t.Name, "T", index + 1),
                t.Celsius.ToString(CultureInfo.InvariantCulture),
                t.SoakMinutesText))
            .ToList();
        if (rows.Count == 0)
            rows.Add(new Views.PointBatchRuleInput("1 20", "25", "120"));

        var hint = "温度录入：范围/序号支持「1 20」（从第 1 个起共 20 点）、「20」（从 T1 起共 20 点）、「1-20」；温度填如 25；保温分钟可留空。例：1 20 + 25 → T1–T20 均为 25℃。";
        var dlg = new Views.PointBatchEditorWindow("批量录入温度点", "温度 (°C)", "保温分钟", hint, rows)
        {
            Owner = Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var points = Core.Config.PointBatchParser.BuildTempPoints(ToRows(dlg.ResultRules)).ToList();
            TempPoints.Clear();
            foreach (var p in points) TempPoints.Add(p);
            AppLog.Info("Config", $"批量录入温度点 {points.Count} 个");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "温度点录入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static IEnumerable<Core.Config.PointBatchRow> ToRows(
        IEnumerable<Views.PointBatchRuleInput> rules) =>
        rules.Select(r => new Core.Config.PointBatchRow(r.Range, r.Value, r.Extra));

    private void RefreshPressurePointRows()
    {
        if (PressurePoints.Count == 0) return;
        var rows = PressurePoints.ToList();
        PressurePoints.Clear();
        foreach (var row in rows) PressurePoints.Add(row);
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
        OnPropertyChanged(nameof(LeakCheckPressuresText));
        OnPropertyChanged(nameof(LeakCheckPrecisionText));
        OnPropertyChanged(nameof(LeakCheckPressuresHint));
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
        Plan.FolderName = folderName;
        var folder = Path.Combine(AppPaths.TestConfigDir, folderName);
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

    private SlotLayoutOptions BuildSlotLayoutOptions() => CurrentSlotLayout.ToOptions();

    private bool SetSlotLayoutField<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var changed = SetField(ref storage, value, name);
        if (changed)
        {
            NotifyBoardLayoutChanged();
            RegenerateSlots();
        }
        return changed;
    }

    private void NotifyBoardLayoutChanged()
    {
        OnPropertyChanged(nameof(BoardCount));
        OnPropertyChanged(nameof(LastBoard));
        OnPropertyChanged(nameof(LastBoardSlot));
        OnPropertyChanged(nameof(BoardMappingSummary));
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
        (_slotCount, _batchNo, _startIndex, _startBoard, _startBoardSlot,
         _boardSlotCapacity, _startValve, _fixtureSlotCapacity, _fixtureCount,
         _startChannel, _startSerial, _autoNumber) =
            CurrentSlotLayout.PatchedFromIni(_settingIni, SlotMax);
    }

    private void SaveSlotLayoutToIni()
    {
        CurrentSlotLayout.SaveToIni(_settingIni);
        _settingIni.Set("Slots", "LastPlan", Plan.Name);
        _settingIni.Set("Slots", "LastPlanFolder", _selectedPlanFolder);
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
        (var pressurePort, var pressureAddress) =
            GpibResource.Parse(_settingIni.Get("Device.Pressure", "Address", pressure?.Address ?? ""));
        PressureGpibPort = pressurePort;
        PressureGpibAddress = pressureAddress;

        var dmm = _session.Station.Get(DeviceKind.Dmm);
        DmmModelName = _settingIni.Get("Device.Dmm", "Model", dmm?.Model ?? DmmModelName);
        (var tempPort, var tempAddress) =
            GpibResource.Parse(_settingIni.Get("Device.Dmm", "Address", dmm?.Address ?? ""));
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
        DelaySettings.Add(new SettingPairVm("探漏泄漏率观测时间", LoadSetting(DelaySection, "LeakCheckSec", "60000"), null, "毫秒(ms)", DelaySection, "LeakCheckSec"));
        DelaySettings.Add(new SettingPairVm("泄压等待时间", LoadSetting(DelaySection, "VentWaitMs", "120000"), null, "毫秒(ms)", DelaySection, "VentWaitMs"));
        DelaySettings.Add(new SettingPairVm("Usig采集延迟", LoadSetting(DelaySection, "UsigDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UsigDelayMs"));
        DelaySettings.Add(new SettingPairVm("UT采集延迟", LoadSetting(DelaySection, "UtDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UtDelayMs"));
        DelaySettings.Add(new SettingPairVm("Usource采集延迟", LoadSetting(DelaySection, "UsourceDelayMs", "300"), null, "毫秒(ms)", DelaySection, "UsourceDelayMs"));
        DelaySettings.Add(new SettingPairVm("Isource采集延迟", LoadSetting(DelaySection, "IsourceDelayMs", "300"), null, "毫秒(ms)", DelaySection, "IsourceDelayMs"));
        DelaySettings.Add(new SettingPairVm("继电器阀门等待时间", LoadSetting(DelaySection, "ValveSwitchMs", "500"), null, "毫秒(ms)", DelaySection, "ValveSwitchMs"));
        DelaySettings.Add(new SettingPairVm("压力稳定连续采样次数", LoadSetting(DelaySection, "PressureStableSamples", "5"), null, "次", DelaySection, "PressureStableSamples"));
        DelaySettings.Add(new SettingPairVm("压力稳定采样间隔", LoadSetting(DelaySection, "PressureStableSampleMs", "500"), null, "毫秒(ms)", DelaySection, "PressureStableSampleMs"));
        DelaySettings.Add(new SettingPairVm("压力稳定超时时间", LoadSetting(DelaySection, "PressureStableTimeoutMs", "120000"), null, "毫秒(ms)", DelaySection, "PressureStableTimeoutMs"));
        DelaySettings.Add(new SettingPairVm("压力自动重稳压次数", LoadSetting(DelaySection, "PressureStabilityRetryCount", "3"), null, "次", DelaySection, "PressureStabilityRetryCount"));
        DelaySettings.Add(new SettingPairVm("阀组采集失败重试次数", LoadSetting(DelaySection, "PressureGroupRetryCount", "2"), null, "次", DelaySection, "PressureGroupRetryCount"));
        DelaySettings.Add(new SettingPairVm("采集过程压力监测间隔", LoadSetting(DelaySection, "PressureMonitorIntervalMs", "1000"), null, "毫秒(ms)", DelaySection, "PressureMonitorIntervalMs"));
        DelaySettings.Add(new SettingPairVm("每隔多少工位监测压力", LoadSetting(DelaySection, "PressureMonitorEverySlots", "4"), null, "工位", DelaySection, "PressureMonitorEverySlots"));
        DelaySettings.Add(new SettingPairVm("压力波动允许差值", LoadSetting(DelaySection, "PressureStabilityTolerance", "0.01"), null, "压力单位", DelaySection, "PressureStabilityTolerance"));
        DelaySettings.Add(new SettingPairVm("重稳压前等待时间", LoadSetting(DelaySection, "PressureRecoveryWaitMs", "1000"), null, "毫秒(ms)", DelaySection, "PressureRecoveryWaitMs"));
        DelaySettings.Add(new SettingPairVm("设定温度等待时间", LoadSetting(DelaySection, "SetTempMs", "10000"), null, "毫秒(ms)", DelaySection, "SetTempMs"));
        DelaySettings.Add(new SettingPairVm("保温时间", LoadSetting(DelaySection, "SoakMinutes", "120"), null, "分钟(min)", DelaySection, "SoakMinutes"));
        DelaySettings.Add(new SettingPairVm("零点校验等待时间", LoadSetting(DelaySection, "ZeroCheckMs", "0"), null, "毫秒(ms)", DelaySection, "ZeroCheckMs"));
    }

    private void LoadPressureCommandSettings()
    {
        PressureCommandSettings.Clear();
        if (string.IsNullOrWhiteSpace(PressureModelName)) return;

        foreach (var (label, key) in new[]
        {
            ("Open", "Open"),
            ("Machine Type", "Machine Type"),
            ("UpperLimit", "UpperLimit"),
            ("SetPressure", "SetPressure"),
            ("Vent", "Vent"),
            ("ZeroCheck", "ZeroCheck"),
            ("ReadPressure", "ReadPressure"),
            ("SetMeasure", "SetMeasure"),
            ("SelfTest", "SelfTest"),
            ("ReadStatus", "ReadStatus"),
            ("SetAbs", "SetAbs"),
            ("SetGaug", "SetGaug"),
            ("SetDiff", "SetDiff"),
        })
        {
            var command = _commands.Raw(PressureModelName, key);
            if (string.IsNullOrWhiteSpace(command) && key == "UpperLimit")
                command = _commands.Raw(PressureModelName, "UpperLimt");
            PressureCommandSettings.Add(new($"{label} 发送指令", command));
        }
    }

    /// <summary>内置默认压力指令行。构造流程中会被随后的 LoadPressureCommandSettings 重填覆盖，
    /// 仅在 ReloadSettings（重载参数，不重读模型指令配置）后作为最终展示内容使用。</summary>
    private void LoadDefaultPressureCommands()
    {
        PressureCommandSettings.Clear();
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
        LoadDefaultPressureCommands();
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
        AppPreferences.Set(_settingIni, "LastPlanFolder", _selectedPlanFolder);
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
        _settingIni.Set("Device.Dmm", "Model", DmmModelName);
        _settingIni.Set("Device.Dmm", "Address", BuildGpibAddress(TempGpibPort, TempGpibAddress));
    }

    private static string NormalizeParity(string value) =>
        value.Equals("N", StringComparison.OrdinalIgnoreCase) ? "None" : value;

    private static string BuildGpibAddress(string port, string address) => GpibResource.Build(port, address);

    private void BuildModelCommands(CommandDictionary commands)
    {
        ModelCommands.Clear();
        CommandDeviceKinds.Clear();
        CommandModels.Clear();
        SelectedModelCommand = null;

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
            ("数字万用表", new[] { "Keysight-34970A", "Keysight-DAQ970A", "Keysight-DAQ973A" },
                new[] { "Open", "Close", "SetVol", "SetRes", "ReadValue", "SelfTest" }),
            ("采集卡",    new[] { "M30-DAC" },
                new[] { "Open", "Usig", "Usource", "Isource", "UT", "SelfTest" }),
            ("通道/板卡",  new[] { "Board" },
                new[] { "Open", "Close", "SelfTest" }),
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

        foreach (var kind in ModelCommands.Select(x => x.Kind).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            CommandDeviceKinds.Add(kind);

        SelectedCommandDeviceKind = CommandDeviceKinds.FirstOrDefault() ?? "";
    }

    private void RefreshCommandModels()
    {
        CommandModels.Clear();
        foreach (var model in ModelCommands
                     .Where(x => string.Equals(x.Kind, SelectedCommandDeviceKind, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase))
        {
            CommandModels.Add(model);
        }

        SelectedModelCommand = CommandModels.FirstOrDefault();
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
        - �ֶ����� TX/RX ���� + �����Ͳ�??(Usig/UT/Usource/Isource/DMM_mV/DMM_R)
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




