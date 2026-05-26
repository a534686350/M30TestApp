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
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class MetricSwitch : ViewModelBase
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
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
    public ObservableCollection<string> PressureModels { get; } = new() { "FLUKE-7250", "FLUKE-6270", "WIKA-CPC8000" };

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

    // ─── §设备 ─────────────────────────────────────────────────────────────
    public ObservableCollection<DeviceProfile> Devices { get; } = new();

    // ─── §工位 ─────────────────────────────────────────────────────────────
    public ObservableCollection<SlotEntry> Slots { get; } = new();

    // ─── §方案 ─────────────────────────────────────────────────────────────
    private TestPlan _plan = new();
    public TestPlan Plan { get => _plan; private set => SetField(ref _plan, value); }
    public string TaskScript => Plan.TaskScript;
    public ObservableCollection<string> PlanNames { get; } = new();
    private string _selectedPlanName = "";
    private bool _loadingPlan;
    public string SelectedPlanName
    {
        get => _selectedPlanName;
        set
        {
            if (!SetField(ref _selectedPlanName, value)) return;
            if (!_loadingPlan && !string.IsNullOrWhiteSpace(value)) LoadPlanByName(value);
        }
    }
    public ObservableCollection<PressurePoint> PressurePoints { get; } = new();
    public ObservableCollection<TempPoint> TempPoints { get; } = new();

    // ─── §指令 ─────────────────────────────────────────────────────────────
    public ObservableCollection<ModelCommandsVm> ModelCommands { get; } = new();

    // ─── §测试流程 ──────────────────────────────────────────────────────────
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

    // ─── §计算 ─────────────────────────────────────────────────────────────
    public ObservableCollection<MetricSwitch> Metrics { get; } = new()
    {
        new() { Code = "Offset",   Name = "零点输出",     Description = "首压力点 / 室温下输出偏置" },
        new() { Code = "Span",     Name = "满量程输出",   Description = "末压力点 - 首压力点" },
        new() { Code = "NL",       Name = "非线性",       Description = "实测曲线与理论拟合直线最大偏差" },
        new() { Code = "PH",       Name = "压力迟滞",     Description = "正反程同压力点输出差" },
        new() { Code = "TCO",      Name = "TCO",          Description = "零点温度系数" },
        new() { Code = "TCS",      Name = "TCS",          Description = "满量程温度系数" },
        new() { Code = "TCR",      Name = "TCR",          Description = "电阻温度系数" },
        new() { Code = "THO",      Name = "温度迟滞",     Description = "升降温同温度点输出差" },
        new() { Code = "Accuracy", Name = "精度",         Description = "综合误差，与精度等级核对" },
    };

    // ─── §版本信息 ─────────────────────────────────────────────────────────
    public string AppVersion => "3.0.0.25 (V2 MVP)";
    public string Changelog { get; }

    // ─── §系统设置 ─────────────────────────────────────────────────────────
    public string BaseDir => AppPaths.BaseDir;
    public string LogDir => AppPaths.LogDir;
    public string DataDir => AppPaths.DataDir;
    public string TestConfigDir => AppPaths.TestConfigDir;
    public ObservableCollection<string> Themes { get; } = new() { "亮色", "暗色 (即将)" };

    private string _selectedTheme = "亮色";
    public string SelectedTheme { get => _selectedTheme; set => SetField(ref _selectedTheme, value); }

    private int _logRetainDays = 30;
    public int LogRetainDays { get => _logRetainDays; set => SetField(ref _logRetainDays, value); }

    // ─── Sub-nav ───────────────────────────────────────────────────────────
    public ObservableCollection<string> Sections { get; } = new()
    {
        "设备", "指令", "工位", "方案", "测试流程", "计算", "版本信息", "系统设置",
    };

    private string _selectedSection = "设备";
    public string SelectedSection { get => _selectedSection; set => SetField(ref _selectedSection, value); }

    // ─── Commands ──────────────────────────────────────────────────────────
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand AddSlotCommand { get; }
    public RelayCommand BatchGenerateSlotsCommand { get; }
    public RelayCommand ImportSlotsCommand { get; }
    public RelayCommand ExportSlotsCommand { get; }
    public RelayCommand NewPlanCommand { get; }
    public RelayCommand BulkEditPressurePointsCommand { get; }
    public RelayCommand BulkEditTempPointsCommand { get; }
    public RelayCommand UsePerformanceFlowCommand { get; }

    public ConfigViewModel(TestSession session)
    {
        _session = session;
        _settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        Plan = session.Plan;
        RefreshPlanNames();
        _selectedPlanName = Plan.Name;
        RefreshComPorts();
        LoadDeviceSettings();
        BuildParameterSettings();

        foreach (var d in session.Station.Devices.Values) Devices.Add(d);
        foreach (var s in session.Slots.Entries) Slots.Add(s);
        foreach (var pp in session.Plan.PressurePoints) PressurePoints.Add(pp);
        foreach (var tp in session.Plan.TempPoints) TempPoints.Add(tp);

        BuildModelCommands(session.Commands);
        BuildTaskSteps(session.Plan.TaskScript);
        Changelog = LoadChangelog();

        SaveCommand = new RelayCommand(_ => SaveSettings());
        ReloadCommand = new RelayCommand(_ => ReloadSettings());
        AddSlotCommand = new RelayCommand(_ => AddSlot());
        BatchGenerateSlotsCommand = new RelayCommand(_ => BatchGenerateSlots());
        ImportSlotsCommand = new RelayCommand(_ => ImportSlots());
        ExportSlotsCommand = new RelayCommand(_ => ExportSlots());
        NewPlanCommand = new RelayCommand(_ => NewPlan());
        BulkEditPressurePointsCommand = new RelayCommand(_ => BulkEditPressurePoints());
        BulkEditTempPointsCommand = new RelayCommand(_ => BulkEditTempPoints());
        UsePerformanceFlowCommand = new RelayCommand(_ => UsePerformanceFlow());
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
        _selectedPlanName = newPlan.Name;
        OnPropertyChanged(nameof(SelectedPlanName));
        AppLog.Info("Config", $"已创建新方案 {newPlan.Name}，点击“保存”后写入 {AppPaths.TestConfigDir}");
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
            AppLog.Info("Config", $"已批量录入压力点 {points.Count} 个");
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
            AppLog.Info("Config", $"已批量录入温度点 {points.Count} 个");
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
        throw new FormatException($"无法解析数值：{line}");
    }

    private void RefreshPlanNames()
    {
        PlanNames.Clear();
        if (Directory.Exists(AppPaths.TestConfigDir))
        {
            foreach (var file in Directory.GetFiles(AppPaths.TestConfigDir, "*.ini").OrderBy(Path.GetFileNameWithoutExtension))
                PlanNames.Add(Path.GetFileNameWithoutExtension(file));
        }
        if (!string.IsNullOrWhiteSpace(Plan.Name) && !PlanNames.Contains(Plan.Name))
            PlanNames.Add(Plan.Name);
    }

    private void LoadPlanByName(string name)
    {
        if (string.Equals(Plan.Name, name, StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(AppPaths.TestConfigDir, name + ".ini");
        if (!File.Exists(path)) return;
        var plan = TestPlan.Load(path);
        SetPlan(plan);
        AppLog.Info("Config", $"已切换方案 {name}");
    }

    private void SetPlan(TestPlan plan)
    {
        _loadingPlan = true;
        Plan = plan;
        _session.Plan = plan;
        _session.Context.Plan = plan;
        _selectedPlanName = plan.Name;
        OnPropertyChanged(nameof(TaskScript));
        OnPropertyChanged(nameof(SelectedPlanName));
        PressurePoints.Clear();
        foreach (var pp in plan.PressurePoints) PressurePoints.Add(pp);
        TempPoints.Clear();
        foreach (var tp in plan.TempPoints) TempPoints.Add(tp);
        _loadingPlan = false;
    }

    private void SavePlan()
    {
        // 把 UI 编辑的点表写回 Plan，并按方案名持久化到 TestConfig 目录。
        Plan.PressurePoints.Clear();
        foreach (var pp in PressurePoints) Plan.PressurePoints.Add(pp);
        Plan.TempPoints.Clear();
        foreach (var tp in TempPoints) Plan.TempPoints.Add(tp);

        var name = string.IsNullOrWhiteSpace(Plan.Name) ? "plan" : Plan.Name;
        Directory.CreateDirectory(AppPaths.TestConfigDir);
        var path = Path.Combine(AppPaths.TestConfigDir, name + ".ini");
        Plan.Save(path);
        RefreshPlanNames();
        if (!PlanNames.Contains(name)) PlanNames.Add(name);
        _selectedPlanName = name;
        OnPropertyChanged(nameof(SelectedPlanName));
        AppLog.Info("Config", $"已保存方案 {name} 到 {path}");
    }

    private void AddSlot()
    {
        var next = Slots.Count + 1;
        Slots.Add(MakeDefaultSlot(next));
        AppLog.Info("Config", $"新增工位 Slot{next}，共 {Slots.Count} 行");
    }

    private void BatchGenerateSlots()
    {
        var dlg = new Views.SlotCountDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Count = Math.Max(Slots.Count, 1)
        };
        if (dlg.ShowDialog() != true) return;
        var target = Math.Clamp(dlg.Count, 1, 256);
        Slots.Clear();
        for (var i = 1; i <= target; i++) Slots.Add(MakeDefaultSlot(i));
        AppLog.Info("Config", $"已批量生成 {target} 个工位");
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
        foreach (var s in Slots)
            sb.AppendLine(string.Join(',', s.Slot, s.SerialNo, s.Valve, s.Board, s.BoardSlotNo,
                s.Layer, s.Fixture, s.FixtureSlotNo, s.PressureController, s.Dmm, s.Channel, s.ValveAddr));
        File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
        AppLog.Info("Config", $"已导出 {Slots.Count} 行工位到 {dlg.FileName}");
    }

    private static SlotEntry MakeDefaultSlot(int index)
    {
        var board = ((index - 1) / 16) + 1;
        var boardSlot = ((index - 1) % 16) + 1;
        var fixture = ((index - 1) / 8) + 1;
        var fixtureSlot = ((index - 1) % 8) + 1;
        return new SlotEntry(
            Slot: $"Slot{index}",
            SerialNo: $"DEMO_{index:D3}",
            Valve: "1",
            Board: board.ToString(),
            BoardSlotNo: boardSlot.ToString(),
            Layer: "1",
            Fixture: fixture.ToString(),
            FixtureSlotNo: fixtureSlot.ToString(),
            PressureController: "1",
            Dmm: "-",
            Channel: "-",
            ValveAddr: "-");
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
        if (ComPorts.Count == 0) ComPorts.Add("(无可用串口)");
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
            TempSensorSettings.Add(new SettingPairVm($"温度变送器{i}", LoadSetting(TempSensorSection, $"Sensor{i}", (200 + i).ToString()), TempChannels, section: TempSensorSection, key: $"Sensor{i}"));

        DelaySettings.Add(new SettingPairVm("加压后等待时间", LoadSetting(DelaySection, "PressureAfterMs", "60000"), null, "毫秒(ms)", DelaySection, "PressureAfterMs"));
        DelaySettings.Add(new SettingPairVm("探漏等待时间", LoadSetting(DelaySection, "LeakWaitMs", "500"), null, "毫秒(ms)", DelaySection, "LeakWaitMs"));
        DelaySettings.Add(new SettingPairVm("泄压等待时间", LoadSetting(DelaySection, "VentWaitMs", "120000"), null, "毫秒(ms)", DelaySection, "VentWaitMs"));
        DelaySettings.Add(new SettingPairVm("稳定读写等待时间", LoadSetting(DelaySection, "StableIoMs", "3000"), null, "毫秒(ms)", DelaySection, "StableIoMs"));
        DelaySettings.Add(new SettingPairVm("开关阀门等待时间", LoadSetting(DelaySection, "ValveSwitchMs", "500"), null, "毫秒(ms)", DelaySection, "ValveSwitchMs"));
        DelaySettings.Add(new SettingPairVm("设定温度等待时间", LoadSetting(DelaySection, "SetTempMs", "10000"), null, "毫秒(ms)", DelaySection, "SetTempMs"));
        DelaySettings.Add(new SettingPairVm("保温时间", LoadSetting(DelaySection, "SoakMinutes", "120"), null, "分钟(min)", DelaySection, "SoakMinutes"));
        DelaySettings.Add(new SettingPairVm("零点校验等待时间", LoadSetting(DelaySection, "ZeroCheckMs", "0"), null, "毫秒(ms)", DelaySection, "ZeroCheckMs"));
        PressureCommandSettings.Add(new("判定型号", "7250"));
        PressureCommandSettings.Add(new("Open函数指令", "*RST;*IDN?"));
        PressureCommandSettings.Add(new("MachineType函数指令", "*IDN?"));
        PressureCommandSettings.Add(new("UpperLimit函数指令", "CALC:LIM:UPP?"));
        PressureCommandSettings.Add(new("SetPressure函数指令", "*CLS;UNIT {0};PRES {1};TOL {2};OUTP:MC"));
        PressureCommandSettings.Add(new("Vent函数指令", "*CLS;OUTP:MODE VENT"));
        PressureCommandSettings.Add(new("ZeroCheck函数指令", "*CLS;CAL:ZERO:INIT;CAL:ZERO:RUN"));
        PressureCommandSettings.Add(new("ReadPressure函数指令", "*CLS;MEAS?"));
        PressureCommandSettings.Add(new("SetMeasure函数指令", "*CLS;OUTP:MODE MEAS"));
        PressureCommandSettings.Add(new("SelfTest函数指令", "*TST?"));
        PressureCommandSettings.Add(new("ReadStatus函数指令", "*CLS;STAT:OPER:COND?"));
        PressureCommandSettings.Add(new("SetGaug函数指令", "*CLS;SENSE:MODE GAUG"));
    }

    private string LoadSetting(string section, string key, string fallback) =>
        _settingIni.Get(section, key, fallback);

    private void SaveSettings()
    {
        SaveDeviceProfiles();
        SavePairs(SwitchUnitCards);
        SavePairs(ValveSettings);
        SavePairs(TempSensorSettings);
        SavePairs(DelaySettings);
        _settingIni.Save(AppPaths.SettingIni);
        _session.Context.Settings = _settingIni;
        SavePlan();
        AppLog.Info("Config", $"已保存参数设置到 {AppPaths.SettingIni}");
    }

    private void ReloadSettings()
    {
        _settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        LoadDeviceSettings();
        BuildParameterSettings();
        AppLog.Info("Config", $"已从 {AppPaths.SettingIni} 重载参数设置");
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
        var slice = new (string Kind, string[] Models, string[] Actions)[]
        {
            ("压力控制器", new[] { "FLUKE-7250", "FLUKE-6270", "WIKA-CPC8000" },
                new[] { "Open", "MachineType", "UpperLimit", "SetPressure", "Vent", "SetAbs",
                        "ZeroCheck", "ReadPressure", "SetMeasure", "SelfTest", "ReadStatus", "SetGaug" }),
            ("烘箱",      new[] { "GWSEBWT1670", "GWNMC2000" },
                new[] { "Open", "Set", "Read", "Stop", "SelfTest" }),
            ("数字万用表", new[] { "Keysight-34970A", "Keysight-DAQ973A" },
                new[] { "Open", "Close", "SetVol", "SetRes", "ReadValue", "SelfTest" }),
            ("采集板",    new[] { "M30-DAC" },
                new[] { "Open", "Usig", "Usource", "Isource", "UT", "SelfTest" }),
            ("通道/阀门",  new[] { "Board" },
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
        # M30TestApp V2 变更
        
        ## 3.0.0.25 - 2026-05-25
        ### 新增
        - WPF 主程序壳 + 亮色主题 + 左侧导航
        - 256 工位上限，SIM 自动生成 + 行/列虚拟化
        - 手动调试 TX/RX 总线 + 多类型采集 (Usig/UT/Usource/Isource/DMM_V/DMM_R)
        - 配置中心 8 子模块占位
        ### 修复
        - AsyncRelayCommand 异常吞噬 → 弹框 + 日志，不再闪退
        - 矩阵列名 DMM-V 触发 Binding 错误 → DataMatrix.SanitizeKey
        
        ## 3.0.0.0 - 2026-05-20
        ### 重构
        - 从 WinForms ASLab 剥离测试核心，重写为 MVVM
        - TaskScript 解释器替代硬编码温/压/工位三重循环
        - DataMatrix 事件流取代 DataGridView 直绑
        - 设备接口化 + SIM/HW 分离
        """;
    }
}
