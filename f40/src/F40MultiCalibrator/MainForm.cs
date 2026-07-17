using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ivi.Visa.Interop;
using Microsoft.Win32;

namespace F40MultiCalibrator;

public sealed class MainForm : Form
{
	private sealed record CompPoint(string Name, double TempDeg, double Pressure, double BridgePercent, int Index);

	private sealed record CompTestPlan(List<double> Temperatures, List<double> Pressures, string Source);

	private sealed record CompTestMeasurement(int Slot, string Serial, double SetTemp, double SetPressure, double ReadPressure, double ReadTemp, double PressureError, double TempError, bool PressurePass, bool TempPass, bool Valid);

	private sealed record CalibrationPlan(string Model, List<double> PressurePoints, string PressureUnit, double OutputMinV, double OutputMaxV, double PercentMin, double PercentMax, double DacToleranceV, bool LinearityEnabled, string Source)
	{
		public double P0 => PressurePoints.Count > 0 ? PressurePoints[0] : 0.0;

		public double Pfull => PressurePoints.Count > 0 ? PressurePoints[^1] : 100.0;

		public double Pmid => PressurePoints.Count >= 3 ? PressurePoints[1] : (P0 + Pfull) / 2.0;
	}

	private sealed record F40TestPlan(
		string Model,
		List<double> Temperatures,
		List<double> Pressures,
		string PressureUnit,
		double PressureZero,
		double PressureFull,
		double OutputMinV,
		double OutputMaxV,
		double AccuracyPercentFs,
		bool ZeroOutputEnabled,
		bool FullOutputEnabled,
		bool PressureHysteresisEnabled,
		bool NonLinearityEnabled,
		bool TemperatureHysteresisEnabled,
		bool TemperatureDriftEnabled,
		bool AccuracyEnabled,
		string Task,
		string UnsupportedReason,
		string Source)
	{
		public double OutputSpanV => OutputMaxV - OutputMinV;

		public double VoltageToleranceV => Math.Abs(OutputSpanV) * Math.Max(0.0, AccuracyPercentFs) / 100.0;

		public bool SupportsDmmMatrix => string.IsNullOrWhiteSpace(UnsupportedReason);
	}

	private sealed record F40TestSlotTemplate(string Serial, string Fixture, string FixtureSlot, string Source);

	private sealed class F40TestSlotData
	{
		public int Slot { get; init; }

		public string Serial { get; init; } = "";

		public string Fixture { get; init; } = "";

		public string FixtureSlot { get; init; } = "";

		public string DmmAddress { get; init; } = "";

		public string Channel { get; init; } = "";

		public double[,] Voltages { get; set; } = new double[0, 0];

		public string Status { get; set; } = "待采集";
	}

	private sealed class CompSlotData
	{
		public int Slot { get; init; }

		public string Serial { get; init; } = "";

		public double[] BridgeRaw { get; } = Enumerable.Repeat(-1.0, 7).ToArray();

		public double[] BridgeDesired { get; } = new double[7];

		public double[] TempRaw { get; } = Enumerable.Repeat(-1.0, 7).ToArray();

		public double[] TempDesired { get; } = new double[7];

		public int[] Coefficients { get; set; } = new int[10];

		public bool Ok { get; set; } = true;

		public string Error { get; set; } = "";

		public string AppliedConfig { get; set; } = "";

		public bool ConfigPassed { get; set; }

		public double P20 { get; set; } = double.NaN;

		public double P80 { get; set; } = double.NaN;

		public double P60 { get; set; } = double.NaN;

		public double PressureAccuracyPermille { get; set; } = double.NaN;

		public double TempAccuracyDeg { get; set; } = double.NaN;
	}

	private sealed record CompConfigCandidate(string Register8, string RegA, string RegB, double AvgP20, double AvgP80, double AvgP60, double PassRate, double ValidRate, int SampleCount);

	private sealed record CompVerifySnapshot(double PressurePercent, double TempDeg, bool Valid);

	private sealed record CompVerifyResult(double P20, double P80, double P60, double LowTempDeg, double HighTempDeg, double PressureAccuracyPermille, double TempAccuracyDeg, bool LowValid, bool HighValid)
	{
		public bool Valid => LowValid && HighValid;

		public bool Pass20 => P20 >= 15.0 && P20 <= 25.0;

		public bool Pass80 => P80 >= 80.0 && P80 <= 85.0;

		public bool Pass60 => P60 >= 60.0;

		public bool PassAll => Pass20 && Pass80 && Pass60;
	}

	private sealed record BoardSlotRoute(byte BoardAddr, int FromSlot, int ToSlot);

	private sealed record BoardSlotTarget(byte BoardAddr, byte LocalSlot, int GlobalSlot);

	private sealed record DaqProfile(int FromSlot, int ToSlot, string Address, string Map);

	private static readonly string BuildTag = $"v{AppUpdateService.CurrentVersionText} · 2026-07-17 工控上位机版";

	private static readonly Color IndustrialHeader = Color.FromArgb(250, 252, 253);

	private static readonly Color IndustrialHeaderBorder = Color.FromArgb(196, 207, 214);

	private static readonly Color IndustrialWorkspace = Color.FromArgb(233, 238, 241);

	private static readonly Color IndustrialSurface = Color.FromArgb(250, 251, 252);

	private static readonly Color IndustrialSurfaceAlt = Color.FromArgb(241, 244, 246);

	private static readonly Color IndustrialText = Color.FromArgb(30, 43, 51);

	private static readonly Color IndustrialMuted = Color.FromArgb(93, 108, 118);

	private static readonly Color IndustrialAccent = Color.FromArgb(0, 137, 151);

	private static readonly Color IndustrialSuccess = Color.FromArgb(25, 137, 87);

	private static readonly Color IndustrialWarning = Color.FromArgb(190, 126, 0);

	private static readonly Color IndustrialDanger = Color.FromArgb(177, 53, 46);

	private static readonly Color IndustrialConsole = Color.FromArgb(248, 250, 251);

	private static readonly Color IndustrialConsoleText = Color.FromArgb(36, 68, 62);

	private const double CompInvalidValue = 25599.999994;

	private const int CalibrationSelectedColumnIndex = 0;

	private readonly TextBox _csvPath = new TextBox
	{
		Width = 650
	};

	private readonly Button _browse = new Button
	{
		Text = "选择原始CSV",
		Width = 105
	};

	private readonly Button _loadCsv = new Button
	{
		Text = "加载",
		Width = 70
	};

	private readonly Button _selectValid = new Button
	{
		Text = "选有效",
		Width = 75
	};

	private readonly Button _selectAll = new Button
	{
		Text = "全选",
		Width = 60
	};

	private readonly Button _selectNone = new Button
	{
		Text = "全不选",
		Width = 70
	};

	private readonly Button _selectDaq60 = new Button
	{
		Text = "选DAQ配置",
		Width = 95
	};

	private readonly Button _selectStableF40Slots = new Button
	{
		Text = "固定32工位",
		Width = 95
	};

	private readonly Button _copyRawDataMap = new Button
	{
		Text = "复用原始数据",
		Width = 105
	};

	private readonly ComboBox _com = new ComboBox
	{
		Width = 95,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly Button _refreshCom = new Button
	{
		Text = "刷新COM",
		Width = 80
	};

	private readonly Button _openSerial = new Button
	{
		Text = "打开板卡",
		Width = 90
	};

	private readonly NumericUpDown _addr = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 247m,
		Value = 1m,
		Width = 50
	};

	private readonly TextBox _boardSlotMap = new TextBox
	{
		Text = "1=1-80;2=81-160",
		Width = 220
	};

	private readonly TextBox _boardSlotMapDevice = new TextBox
	{
		Text = "1=1-80;2=81-160",
		Width = 220
	};

	private readonly TextBox _boardSlotMapCal = new TextBox
	{
		Text = "1=1-80;2=81-160",
		Width = 220
	};

	private readonly CheckBox _useBoardChannel47 = new CheckBox
	{
		Text = "使用4/7通道",
		Checked = true,
		Width = 115
	};

	private readonly CheckBox _useBoardChannel47Manual = new CheckBox
	{
		Text = "使用4/7通道",
		Checked = true,
		Width = 115
	};

	private readonly NumericUpDown _timeout = new NumericUpDown
	{
		Minimum = 100m,
		Maximum = 10000m,
		Value = 1500m,
		Increment = 100m,
		Width = 70
	};

	private readonly CheckBox _useGpib = new CheckBox
	{
		Text = "使用GPIB",
		Checked = true,
		Width = 85
	};

	private readonly ComboBox _pressureAddr = new ComboBox
	{
		Text = "GPIB0::8::INSTR",
		Width = 145,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _dmmAddr = new ComboBox
	{
		Text = "GPIB0::22::INSTR",
		Width = 145,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly CheckBox _useDaqChannel = new CheckBox
	{
		Text = "DMM/DAQ通道",
		Checked = true,
		Width = 110
	};

	private readonly TextBox _channelExpr = new TextBox
	{
		Text = "DAQ973A60",
		Width = 120
	};

	private readonly TextBox _daqChannelOverrideMap = new TextBox
	{
		Text = "",
		Width = 520,
		PlaceholderText = "工位=DAQ通道：1=101;9=102;17=103;33=213 或 1-20=101-120;21-40=201-220"
	};

	private readonly Button _applyChannelMap = new Button
	{
		Text = "应用通道",
		Width = 86
	};

	private readonly Button _applyChannelMapDevice = new Button
	{
		Text = "应用通道",
		Width = 86
	};

	private readonly Button _fillChannelSequence = new Button
	{
		Text = "顺填通道",
		Width = 86
	};

	private readonly Button _fillChannelSequenceRun = new Button
	{
		Text = "顺填通道",
		Width = 86
	};

	private readonly CheckBox _multiDaq = new CheckBox
	{
		Text = "多台DAQ973A",
		Checked = true,
		Width = 115
	};

	private readonly TextBox _daqProfiles = new TextBox
	{
		Text = "1-60=GPIB0::22::INSTR;DAQ973A60\r\n61-120=GPIB0::23::INSTR;DAQ973A60",
		Multiline = true,
		Width = 520,
		Height = 74,
		ScrollBars = ScrollBars.Vertical,
		Visible = false
	};

	private readonly DataGridView _daqProfileGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = true,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly Button _refreshVisa = new Button
	{
		Text = "扫描VISA",
		Width = 86
	};

	private readonly NumericUpDown _p0 = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 0m,
		Width = 80
	};

	private readonly NumericUpDown _pmid = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 50m,
		Width = 80
	};

	private readonly NumericUpDown _pfull = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 100m,
		Width = 80
	};

	private readonly ComboBox _pressureUnit = new ComboBox
	{
		Width = 60,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _calSensorModel = new ComboBox
	{
		Width = 150,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly Button _applyCalModel = new Button
	{
		Text = "应用型号压力",
		Width = 105
	};

	private readonly NumericUpDown _stableTolKpa = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 1000m,
		DecimalPlaces = 3,
		Value = 0.5m,
		Increment = 0.1m,
		Width = 75
	};

	private readonly NumericUpDown _stableSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 600m,
		DecimalPlaces = 1,
		Value = 5m,
		Increment = 0.5m,
		Width = 65
	};

	private readonly NumericUpDown _settleSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 600m,
		DecimalPlaces = 1,
		Value = 2m,
		Increment = 0.5m,
		Width = 65
	};

	private readonly NumericUpDown _calVoltageTolerance = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 1m,
		DecimalPlaces = 3,
		Value = 0.008m,
		Increment = 0.001m,
		Width = 70
	};

	private readonly NumericUpDown _calOutputMinV = new NumericUpDown
	{
		Minimum = -10m,
		Maximum = 10m,
		DecimalPlaces = 3,
		Value = 0.5m,
		Increment = 0.001m,
		Width = 70
	};

	private readonly NumericUpDown _calOutputMaxV = new NumericUpDown
	{
		Minimum = -10m,
		Maximum = 10m,
		DecimalPlaces = 3,
		Value = 4.5m,
		Increment = 0.001m,
		Width = 70
	};

	private readonly NumericUpDown _calPercentMin = new NumericUpDown
	{
		Minimum = -1000m,
		Maximum = 1000m,
		DecimalPlaces = 2,
		Value = 10m,
		Increment = 0.01m,
		Width = 70
	};

	private readonly NumericUpDown _calPercentMax = new NumericUpDown
	{
		Minimum = -1000m,
		Maximum = 1000m,
		DecimalPlaces = 2,
		Value = 90m,
		Increment = 0.01m,
		Width = 70
	};

	private readonly NumericUpDown _calMaxRetryCount = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 999m,
		DecimalPlaces = 0,
		Value = 0m,
		Width = 68
	};

	private readonly CheckBox _preserveTempCoe = new CheckBox
	{
		Text = "保留温补3系数(原版)",
		Checked = true,
		Enabled = false,
		Width = 155
	};

	private readonly CheckBox _writeBoard = new CheckBox
	{
		Text = "写入板卡0x11",
		Checked = true,
		Width = 115
	};

	private readonly CheckBox _verifyAfterWrite = new CheckBox
	{
		Text = "写后复测",
		Checked = true,
		Width = 85
	};

	private readonly CheckBox _calLinearityEnabled = new CheckBox
	{
		Text = "中点线性",
		Checked = true,
		Width = 85
	};

	private readonly CheckBox _batchPressureMode = new CheckBox
	{
		Text = "批量稳压加速(非原节奏)",
		Checked = false,
		Width = 180
	};

	private readonly CheckBox _writeConfigBeforeCal = new CheckBox
	{
		Text = "标定前产品配置",
		Checked = false,
		Width = 135
	};

	private readonly NumericUpDown _preCalBoardAddr = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 247m,
		Value = 1m,
		Width = 58
	};

	private readonly ComboBox _preCalConfigGroup = new ComboBox
	{
		Width = 78,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly TextBox _preCalRegAHex = new TextBox
	{
		Text = "0001",
		Width = 70
	};

	private readonly TextBox _preCalRegBHex = new TextBox
	{
		Text = "0267",
		Width = 70
	};

	private readonly NumericUpDown _preCalStartSlot = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 1m,
		Width = 58
	};

	private readonly NumericUpDown _preCalConfigCount = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 8m,
		Width = 58
	};

	private readonly Button _writePreCalConfig = new Button
	{
		Text = "先写配置",
		Width = 105
	};

	private readonly Button _calcSelected = new Button
	{
		Text = "只计算选中",
		Width = 100
	};

	private readonly Button _writeSelected = new Button
	{
		Text = "只写选中系数",
		Width = 115
	};

	private readonly Button _start = new Button
	{
		Text = "开始自动标定",
		Width = 115
	};

	private readonly NumericUpDown _singleCalSlot = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 1m,
		Width = 70
	};

	private readonly Button _startSingleCal = new Button
	{
		Text = "开始单独标定",
		Width = 115
	};

	private readonly Button _stop = new Button
	{
		Text = "停止",
		Width = 70,
		Enabled = false
	};

	private readonly DataGridView _grid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AutoGenerateColumns = false,
		AllowUserToAddRows = false,
		RowHeadersVisible = false
	};

	private readonly TextBox _log = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		Font = new Font("Consolas", 10f)
	};

	private readonly TextBox _logFull = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		ReadOnly = true,
		Font = new Font("Consolas", 10f)
	};

	private readonly TextBox _logManual = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		ReadOnly = true,
		Font = new Font("Consolas", 10f)
	};

	private readonly TextBox _logComp = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		ReadOnly = true,
		Font = new Font("Consolas", 10f)
	};

	private readonly TextBox _logCompManual = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		ReadOnly = true,
		Font = new Font("Consolas", 10f)
	};

	private readonly Label _devBoardState = new Label();

	private readonly Label _devPressureState = new Label();

	private readonly Label _devDaqState = new Label();

	private readonly Label _devOvenState = new Label();

	private readonly List<(Label Board, Label Pressure, Label Daq, Label Oven)> _deviceStatusViews = new List<(Label, Label, Label, Label)>();

	private Label? _headerBoardPill = null;

	private Label? _headerPressurePill = null;

	private Label? _headerDaqPill = null;

	private Label? _headerOvenPill = null;

	private Label? _headerRunPill = null;

	private Label? _calRunStateLabel = null;

	private Label? _calStageLabel = null;

	private Label? _calProgressLabel = null;

	private Label? _calRecipeSummaryLabel = null;

	private Label? _calInterlockLabel = null;

	private readonly DataGridView _manualRawGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = false,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly DataGridView _compGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = false,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly ComboBox _compSensorModel = new ComboBox
	{
		Width = 270,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly CheckBox _compUseOven = new CheckBox
	{
		Text = "烘箱",
		Checked = true,
		Width = 70
	};

	private readonly CheckBox _compUseDebug = new CheckBox
	{
		Text = "调零",
		Checked = true,
		Width = 70
	};

	private readonly CheckBox _compAutoConfig = new CheckBox
	{
		Text = "自动配置",
		Width = 92
	};

	private readonly CheckBox _compTest = new CheckBox
	{
		Text = "补偿后验证",
		Width = 105
	};

	private readonly CheckBox _compWriteNumber = new CheckBox
	{
		Text = "写编号",
		Width = 78
	};

	private readonly ComboBox _compOvenModel = new ComboBox
	{
		Width = 135,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly NumericUpDown _compStartSlot = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 1m,
		Width = 58
	};

	private readonly NumericUpDown _compSlotCount = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 16m,
		Width = 58
	};

	private readonly NumericUpDown _compP0 = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 0m,
		Width = 75
	};

	private readonly NumericUpDown _compP50 = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 50m,
		Width = 75
	};

	private readonly NumericUpDown _compP100 = new NumericUpDown
	{
		Minimum = -100000m,
		Maximum = 100000m,
		DecimalPlaces = 3,
		Value = 100m,
		Width = 75
	};

	private readonly ComboBox _compPressureUnit = new ComboBox
	{
		Width = 60,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly NumericUpDown _compT1 = new NumericUpDown
	{
		Minimum = -80m,
		Maximum = 200m,
		DecimalPlaces = 1,
		Value = 5m,
		Width = 65
	};

	private readonly NumericUpDown _compT2 = new NumericUpDown
	{
		Minimum = -80m,
		Maximum = 200m,
		DecimalPlaces = 1,
		Value = 25m,
		Width = 65
	};

	private readonly NumericUpDown _compT3 = new NumericUpDown
	{
		Minimum = -80m,
		Maximum = 200m,
		DecimalPlaces = 1,
		Value = 45m,
		Width = 65
	};

	private readonly NumericUpDown _compTempTol = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 50m,
		DecimalPlaces = 1,
		Value = 1m,
		Increment = 0.5m,
		Width = 58
	};

	private readonly NumericUpDown _compTempHoldSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 36000m,
		Value = 30m,
		Increment = 10m,
		Width = 70
	};

	private readonly NumericUpDown _compPressureHoldSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 36000m,
		Value = 10m,
		Increment = 5m,
		Width = 70
	};

	private readonly CheckBox _compWritePreConfig = new CheckBox
	{
		Text = "先写0304配置",
		Checked = true,
		Width = 120
	};

	private readonly CheckBox _compWriteCoefficients = new CheckBox
	{
		Text = "算完写0x11",
		Checked = false,
		Width = 105
	};

	private readonly TextBox _compOutputDir = new TextBox
	{
		Width = 360
	};

	private readonly Button _compBrowseOutput = new Button
	{
		Text = "输出目录",
		Width = 85
	};

	private readonly Button _compStart = new Button
	{
		Text = "开始自动补偿",
		Width = 125,
		Height = 34,
		BackColor = Color.FromArgb(20, 184, 166)
	};

	private readonly Button _compStop = new Button
	{
		Text = "停止",
		Width = 78,
		Height = 34,
		Enabled = false
	};

	private readonly ComboBox _testSensorModel = new ComboBox
	{
		Width = 230,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly NumericUpDown _testStartSlot = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 1m,
		Width = 58
	};

	private readonly NumericUpDown _testSlotCount = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 40m,
		Width = 58
	};

	private readonly CheckBox _testUsePressure = new CheckBox
	{
		Text = "压力控制器",
		Checked = true,
		Width = 98
	};

	private readonly CheckBox _testUseOven = new CheckBox
	{
		Text = "烘箱",
		Checked = true,
		Width = 62
	};

	private readonly CheckBox _testUseDmm = new CheckBox
	{
		Text = "DMM/DAQ",
		Checked = true,
		Width = 88
	};

	private readonly NumericUpDown _testTempHoldSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 36000m,
		Value = 120m,
		Increment = 30m,
		Width = 74
	};

	private readonly NumericUpDown _testPressureHoldSec = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 36000m,
		Value = 30m,
		Increment = 5m,
		Width = 74
	};

	private readonly NumericUpDown _testVoltageTolerance = new NumericUpDown
	{
		Minimum = 0m,
		Maximum = 1m,
		DecimalPlaces = 4,
		Value = 0.0100m,
		Increment = 0.001m,
		Width = 76
	};

	private readonly TextBox _testOutputDir = new TextBox
	{
		Width = 360
	};

	private readonly Button _testBrowseOutput = new Button
	{
		Text = "输出目录",
		Width = 82
	};

	private readonly Button _testLoadPlan = new Button
	{
		Text = "加载测试方案",
		Width = 112
	};

	private readonly Button _testRefreshSlots = new Button
	{
		Text = "刷新工位",
		Width = 88
	};

	private readonly Button _testStart = new Button
	{
		Text = "开始采集",
		Width = 110,
		Height = 34,
		BackColor = Color.FromArgb(20, 184, 166)
	};

	private readonly Button _testStop = new Button
	{
		Text = "停止",
		Width = 78,
		Height = 34,
		Enabled = false
	};

	private readonly DataGridView _testGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = false,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly TextBox _logTest = new TextBox
	{
		Dock = DockStyle.Fill,
		Multiline = true,
		ScrollBars = ScrollBars.Both,
		WordWrap = false,
		ReadOnly = true,
		Font = new Font("Consolas", 10f)
	};

	private readonly BindingList<F40SlotRow> _rows = new BindingList<F40SlotRow>();

	private List<F40SlotRow> _loadedCalibrationRows = new List<F40SlotRow>();

	private SerialBoardClient? _board;

	private CancellationTokenSource? _cts;

	private F40TestPlan? _currentF40TestPlan;

	private string _logFile = "";

	private int _lastCalibrationCheckRowIndex = -1;

	private readonly ToolStripStatusLabel _statusSerial = new ToolStripStatusLabel("板卡：未连接");

	private readonly ToolStripStatusLabel _statusCsv = new ToolStripStatusLabel("CSV：未加载");

	private readonly ToolStripStatusLabel _statusSelected = new ToolStripStatusLabel("选中：0");

	private readonly NumericUpDown _manualSlot = new NumericUpDown
	{
		Minimum = 1m,
		Maximum = 255m,
		Value = 1m,
		Width = 70
	};

	private readonly ComboBox _manualFunction = new ComboBox
	{
		Width = 170,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly TextBox _manualPayload = new TextBox
	{
		Width = 280
	};

	private readonly NumericUpDown _manualExpectedLen = new NumericUpDown
	{
		Minimum = 4m,
		Maximum = 255m,
		Value = 5m,
		Width = 70
	};

	private readonly Button _manualSend = new Button
	{
		Text = "发送指令",
		Width = 100
	};

	private readonly Button _quickPing = new Button
	{
		Text = "AA 通信测试",
		Width = 120
	};

	private readonly Button _quickReadRaw = new Button
	{
		Text = "02 读原始",
		Width = 110
	};

	private readonly Button _quickReadCal = new Button
	{
		Text = "12 读补偿",
		Width = 110
	};

	private readonly Button _quickEnterOwi = new Button
	{
		Text = "63 进OWI",
		Width = 100
	};

	private readonly Button _quickExitOwi = new Button
	{
		Text = "61 退OWI",
		Width = 100
	};

	private readonly Button _openLogDir = new Button
	{
		Text = "打开日志目录",
		Width = 120
	};

	private readonly ComboBox _boardBaud = new ComboBox
	{
		Width = 95,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _boardDataBits = new ComboBox
	{
		Width = 95,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _boardParity = new ComboBox
	{
		Width = 95,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _boardStopBits = new ComboBox
	{
		Width = 95,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _ovenIp = new ComboBox
	{
		Width = 150,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _ovenPort = new ComboBox
	{
		Width = 85,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _ovenUnitId = new ComboBox
	{
		Width = 85,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _ovenCom = new ComboBox
	{
		Width = 120,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _ovenBaud = new ComboBox
	{
		Width = 120,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _ovenDataBits = new ComboBox
	{
		Width = 120,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _ovenParity = new ComboBox
	{
		Width = 120,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _ovenStopBits = new ComboBox
	{
		Width = 120,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _pressureModel = new ComboBox
	{
		Width = 170,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _pressureGpibAddress = new ComboBox
	{
		Width = 75,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _pressureGpibPort = new ComboBox
	{
		Width = 75,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _dmmModel = new ComboBox
	{
		Width = 190,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly ComboBox _dmmGpibAddress = new ComboBox
	{
		Width = 75,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly ComboBox _dmmGpibPort = new ComboBox
	{
		Width = 75,
		DropDownStyle = ComboBoxStyle.DropDownList
	};

	private readonly CheckBox _daqSkipChannel47 = new CheckBox
	{
		Text = "DAQ跳过4/7",
		Checked = true,
		Width = 105
	};

	private readonly ComboBox[] _daqCardChannels = (from i in Enumerable.Range(1, 16)
		select new ComboBox
		{
			Width = 126,
			DropDownStyle = ComboBoxStyle.DropDown
		}).ToArray();

	private readonly DataGridView _deviceGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = true,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly DataGridView _commandGrid = new DataGridView
	{
		Dock = DockStyle.Fill,
		AllowUserToAddRows = true,
		RowHeadersVisible = false,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
	};

	private readonly ComboBox _commandModel = new ComboBox
	{
		Width = 230,
		DropDownStyle = ComboBoxStyle.DropDown
	};

	private readonly Button _loadCommandModel = new Button
	{
		Text = "加载型号指令",
		Width = 110
	};

	private readonly Button _saveConfig = new Button
	{
		Text = "保存",
		Width = 92
	};

	private readonly Button _reloadConfig = new Button
	{
		Text = "重载",
		Width = 92
	};

	private readonly Button _checkUpdate = new Button
	{
		Text = "检测更新",
		Width = 92
	};

	private readonly Button _importIniCommand = new Button
	{
		Text = "导入指令INI",
		Width = 115
	};

	private readonly TextBox _planName = new TextBox
	{
		Text = "F40-100psi-MultiDAQ",
		Width = 230
	};

	private readonly TextBox _configHint = new TextBox
	{
		ReadOnly = true,
		BorderStyle = BorderStyle.None,
		BackColor = Color.White,
		Dock = DockStyle.Fill,
		Multiline = true
	};

	private readonly Dictionary<string, Dictionary<string, string>> _commands = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

	private bool _syncBoardAddrBusy;

	private bool _syncGpibBusy;

	private const double OriginalCalibrationOutputToleranceV = 0.01;

	private const int OriginalPhaseMaxAdjustCount = 5;

	private const double WriteNoEffectThresholdV = 0.01;

	private string SettingDir => Path.Combine(AppContext.BaseDirectory, "setting");

	private string SettingPath => Path.Combine(SettingDir, "Setting.ini");

	private string CommandPath => Path.Combine(SettingDir, "Command.ini");

	private double CalibrationOutputToleranceV => (double)_calVoltageTolerance.Value;

	private double EffectiveAutoCalibrationToleranceV => Math.Min(CalibrationOutputToleranceV, Math.Abs(CalibrationTargetMaxV - CalibrationTargetMinV));

	private double CalibrationTargetMinV => (double)_calOutputMinV.Value;

	private double CalibrationTargetMaxV => (double)_calOutputMaxV.Value;

	private double CalibrationTargetPercentMin => (double)_calPercentMin.Value;

	private double CalibrationTargetPercentMax => (double)_calPercentMax.Value;

	private bool CalibrationLinearityEnabled => _calLinearityEnabled.Checked;

	private int CalibrationMaxRetryCount => (int)_calMaxRetryCount.Value;

	private string PressureUnitText => NormalizePressureUnit(_pressureUnit.Text);

	private int EffectiveBoardSlotCount => GetBoardPhysicalSlots().Length;

	public MainForm()
	{
		Text = $"F40 补偿/标定/测试工作站 v{AppUpdateService.CurrentVersionText}";
		try
		{
			base.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? base.Icon;
		}
		catch
		{
		}
		base.Width = 1500;
		base.Height = 950;
		MinimumSize = new Size(1280, 760);
		base.StartPosition = FormStartPosition.CenterScreen;
		Font = new Font("Microsoft YaHei UI", 9f);
		_pressureUnit.Items.AddRange(new object[2] { "psi", "kPa" });
		_pressureUnit.SelectedItem = "psi";
		_calSensorModel.Items.AddRange(new object[19] { "F40_100psi", "F40_100psi-1", "150psi", "15psi", "30psi", "250psi", "1MPa", "F10_1MPa", "F40_600K", "-7kpa-----7kpa", "3points", "F40_150psi", "F40_30psi", "F40_250psi", "F40_1MPa", "F40_0.6MPa", "F40_100kPa", "F40_500kPa", "A1m" });
		_calSensorModel.Text = "F40_100psi";
		_testSensorModel.Items.AddRange(new object[12] { "F40_100psi", "F40_150psi", "F40_15psi", "F40_30psi", "F40_250psi", "F40_1MPa", "F40_100psi_001", "F40_100psi_老化", "F40线性", "F10_1MPa", "F10_1MPa_单温迟滞", "FPS16V" });
		_testSensorModel.Text = "F40_100psi";
		_compPressureUnit.Items.AddRange(new object[2] { "psi", "kPa" });
		_compPressureUnit.SelectedItem = "psi";
		_preCalConfigGroup.Items.AddRange(new object[2] { "0304", "1415" });
		_preCalConfigGroup.SelectedItem = "0304";
		_manualFunction.Items.AddRange(new object[8] { "AA 通信测试", "02 读原始", "12 读补偿", "63 进入OWI", "61 退出OWI", "76 读IIC", "11 写当前行系数", "自定义功能码" });
		_manualFunction.SelectedIndex = 1;
		_csvPath.Text = "C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\软件补偿\\软件补偿\\data\\原始数据\\F40_100psi_表压_原始数据2605182317.csv";
		InitConfigControls();
		EnsureDefaultConfigFiles();
		LoadAppConfig();
		LoadCommandFile();
		bool ovenCommandsMigrated = EnsureOriginalTcpOvenCommands();
		SetupGrid();
		BuildIndustrialShell();
		WireEvents();
		RefreshPorts();
		InitLog();
		if (ovenCommandsMigrated)
		{
			Log("已在内存中把SIDAUMC1000旧Modbus指令兼容为原程序TCP文本协议，未修改Command.ini。", important: true);
		}
		base.Shown += delegate
		{
			LoadCsvSafe(_csvPath.Text);
		};
	}

	private void InitConfigControls()
	{
		_com.DropDownWidth = 120;
		_ovenCom.DropDownWidth = 120;
		_pressureModel.DropDownWidth = 190;
		_pressureAddr.DropDownWidth = 220;
		_dmmModel.DropDownWidth = 200;
		_dmmAddr.DropDownWidth = 220;
		_calSensorModel.DropDownWidth = 170;
		ComboBox[] array = new ComboBox[2] { _boardBaud, _ovenBaud };
		foreach (ComboBox comboBox in array)
		{
			comboBox.Items.AddRange(new object[5] { "9600", "19200", "38400", "57600", "115200" });
		}
		ComboBox[] array2 = new ComboBox[2] { _boardDataBits, _ovenDataBits };
		foreach (ComboBox comboBox2 in array2)
		{
			comboBox2.Items.AddRange(new object[2] { "7", "8" });
		}
		ComboBox[] array3 = new ComboBox[2] { _boardParity, _ovenParity };
		foreach (ComboBox comboBox3 in array3)
		{
			comboBox3.Items.AddRange(new object[3] { "None", "Odd", "Even" });
		}
		ComboBox[] array4 = new ComboBox[2] { _boardStopBits, _ovenStopBits };
		foreach (ComboBox comboBox4 in array4)
		{
			comboBox4.Items.AddRange(new object[2] { "1", "2" });
		}
		_boardBaud.Text = "9600";
		_boardDataBits.Text = "8";
		_boardParity.Text = "None";
		_boardStopBits.Text = "1";
		_ovenIp.Text = "169.254.174.136";
		_ovenPort.Text = "508";
		_ovenUnitId.Items.AddRange(new object[3] { "0", "1", "255" });
		_ovenUnitId.Text = "0";
		_ovenBaud.Text = "19200";
		_ovenDataBits.Text = "8";
		_ovenParity.Text = "None";
		_ovenStopBits.Text = "1";
		_ovenCom.Text = "COM5";
		_pressureModel.Items.AddRange(new object[7] { "DRUCK-PACE5000", "DRUCK-PACE6000", "FLUKE-7250", "FLUKE-6270A", "WIKA-CPC6050", "WIKA-CPC8000", "ConST-860" });
		_pressureModel.Text = "DRUCK-PACE5000";
		ComboBox[] array5 = new ComboBox[2] { _pressureGpibPort, _dmmGpibPort };
		foreach (ComboBox comboBox5 in array5)
		{
			comboBox5.Items.AddRange(Enumerable.Range(0, 10).Select((Func<int, object>)((int x) => x.ToString(CultureInfo.InvariantCulture))).ToArray());
			comboBox5.Text = "0";
		}
		ComboBox[] array6 = new ComboBox[2] { _pressureGpibAddress, _dmmGpibAddress };
		foreach (ComboBox comboBox6 in array6)
		{
			comboBox6.Items.AddRange(Enumerable.Range(0, 31).Select((Func<int, object>)((int x) => x.ToString(CultureInfo.InvariantCulture))).ToArray());
		}
		_pressureGpibAddress.Text = "8";
		_dmmGpibAddress.Text = "22";
		SetComboText(_pressureAddr, "GPIB0::8::INSTR");
		SetComboText(_dmmAddr, "GPIB0::22::INSTR");
		_compOvenModel.Items.AddRange(new object[2] { "GWSEBWT1670", "SIDAUMC1000" });
		_compOvenModel.Text = "SIDAUMC1000";
		_compOutputDir.Text = "C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\软件补偿\\软件补偿\\data\\原始数据";
		_testOutputDir.Text = "C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\F40测试\\data";
		_dmmModel.Items.AddRange(new object[3] { "Keysight-DAQ973A", "Keysight-34970A", "Keysight-34461" });
		_dmmModel.Text = "Keysight-DAQ973A";
		for (int num2 = 0; num2 < _daqCardChannels.Length; num2++)
		{
			ComboBox comboBox7 = _daqCardChannels[num2];
			comboBox7.Items.AddRange(Enumerable.Range(101, 20).Concat(Enumerable.Range(201, 20)).Concat(Enumerable.Range(301, 20))
				.Select((Func<int, object>)((int x) => x.ToString()))
				.ToArray());
			comboBox7.Text = (301 + num2).ToString(CultureInfo.InvariantCulture);
		}
		SetupDeviceGrid();
		SetupCommandGrid();
		SetupDaqProfileGrid();
		SetupCompensationGrid();
		SetupF40TestGrid();
		SetupManualRawGrid();
	}

	private void EnsureDefaultConfigFiles()
	{
		Directory.CreateDirectory(SettingDir);
		if (!File.Exists(SettingPath))
		{
			File.WriteAllText(SettingPath, ApplyCurrentOvenDefaults(DefaultSettingIni()), Encoding.UTF8);
		}
		if (!File.Exists(CommandPath))
		{
			File.WriteAllText(CommandPath, DefaultCommandIni(), Encoding.UTF8);
		}
	}

	private static string ApplyCurrentOvenDefaults(string text)
	{
		return text
			.Replace("[Device.Oven]\nModel = \"Oven\"\nAddress = \"COM5\"\nBaud = \"19200\"", "[Device.Oven]\nModel = \"SIDAUMC1000\"\nAddress = \"169.254.174.136\"\nIp = \"169.254.174.136\"\nPort = \"508\"\nUnitId = \"0\"\nCom = \"COM5\"\nBaud = \"19200\"", StringComparison.Ordinal)
			.Replace("OvenModel = \"GWSEBWT1670\"", "OvenModel = \"SIDAUMC1000\"", StringComparison.Ordinal);
	}

	private static string DefaultSettingIni()
	{
		return "[Plan]\nName = \"F40-100psi-MultiDAQ\"\nRawCsv = \"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\软件补偿\\软件补偿\\data\\原始数据\\F40_100psi_表压_原始数据2605182317.csv\"\n\n[Device.Board]\nModel = \"Board\"\nAddress = \"COM3\"\nBaud = \"9600\"\nDataBits = \"8\"\nParity = \"None\"\nStopBits = \"1\"\nStation = \"1\"\nSlotMap = \"1=1-80;2=81-160\"\nUseChannel47 = \"TRUE\"\nTimeoutMs = \"1500\"\n\n[Device.Oven]\nModel = \"SIDAUMC1000\"\nAddress = \"169.254.174.136\"\nIp = \"169.254.174.136\"\nPort = \"508\"\nUnitId = \"0\"\nCom = \"COM5\"\nBaud = \"19200\"\nDataBits = \"8\"\nParity = \"None\"\nStopBits = \"1\"\n\n[Device.Pressure]\nModel = \"DRUCK-PACE5000\"\nAddress = \"GPIB0::8::INSTR\"\nGpibPort = \"0\"\nGpibAddress = \"8\"\nMode = \"Hw\"\n\n[Device.Dmm]\nModel = \"Keysight-DAQ973A\"\nAddress = \"GPIB0::22::INSTR\"\nGpibPort = \"0\"\nGpibAddress = \"22\"\nMode = \"Hw\"\n\n[DAQ]\nUseChannel = \"TRUE\"\nMultiDaq = \"TRUE\"\nSkipChannel47 = \"TRUE\"\nDefaultMap = \"DAQ973A60\"\nManualChannelMap = \"\"\nProfile0 = \"1-80=GPIB0::22::INSTR;DAQ973A60\"\nProfile1 = \"81-160=GPIB0::23::INSTR;DAQ973A60\"\nCard1 = \"301\"\nCard2 = \"302\"\nCard3 = \"303\"\nCard4 = \"304\"\nCard5 = \"305\"\nCard6 = \"306\"\nCard7 = \"307\"\nCard8 = \"308\"\nCard9 = \"309\"\nCard10 = \"310\"\nCard11 = \"311\"\nCard12 = \"312\"\nCard13 = \"313\"\nCard14 = \"314\"\nCard15 = \"315\"\nCard16 = \"316\"\n\n[Calibration]\nSensorModel = \"F40_100psi\"\nP0 = \"0\"\nPmid = \"50\"\nPfull = \"100\"\nUnit = \"psi\"\nStableTolKpa = \"0.5\"\nStableSec = \"5\"\nSettleSec = \"2\"\nOutputMinV = \"0.500\"\nOutputMaxV = \"4.500\"\nPercentMin = \"10.00\"\nPercentMax = \"90.00\"\nOutputTolV = \"0.008\"\nLinearityEnabled = \"TRUE\"\nMaxRetryCount = \"0\"\nPreserveTempCoefficients = \"TRUE\"\nWriteBoard = \"TRUE\"\nVerifyAfterWrite = \"TRUE\"\nBatchPressureMode = \"FALSE\"\nWriteConfigBeforeCal = \"FALSE\"\nPreCalConfigGroup = \"0304\"\nPreCalRegAHex = \"0001\"\nPreCalRegBHex = \"0267\"\nPreCalStartSlot = \"1\"\nPreCalConfigCount = \"80\"\n\n[Compensation]\nSensorModel = \"F40_100psi\"\nUseOven = \"TRUE\"\nUseDebug = \"TRUE\"\nAutoConfig = \"TRUE\"\nTestAfterWrite = \"FALSE\"\nWriteNumber = \"FALSE\"\nWritePreConfig = \"TRUE\"\nWriteCoefficients = \"FALSE\"\nOvenModel = \"SIDAUMC1000\"\nStartSlot = \"1\"\nSlotCount = \"16\"\nP0 = \"0\"\nP50 = \"50\"\nP100 = \"100\"\nPressureUnit = \"psi\"\nT1 = \"5\"\nT2 = \"25\"\nT3 = \"45\"\nTempTol = \"1\"\nTempHoldSec = \"30\"\nPressureHoldSec = \"10\"\nOutputDir = \"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\软件补偿\\软件补偿\\data\\原始数据\"\n\n[F40Test]\nSensorModel = \"F40_100psi\"\nStartSlot = \"1\"\nSlotCount = \"40\"\nUsePressure = \"TRUE\"\nUseOven = \"TRUE\"\nUseDmm = \"TRUE\"\nTempHoldSec = \"120\"\nPressureHoldSec = \"30\"\nVoltageTolerance = \"0.0100\"\nOutputDir = \"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\F40测试\\data\"";
	}

	private static string DefaultCommandIni()
	{
		return "[DRUCK-PACE5000]\nOpen = \"*RST\"\nMachineType = \"*CLS;*IDN?\"\nReadPressure = \"*CLS;SENS?\"\nSetPressure = \"*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON\"\nVent = \"*CLS;:Sour:Vent 1;:CAL:PRES:ZERP:VALV;*CLS;:OUTPUT OFF;*CLS;:OUTPUT ON\"\nSetGaug = \":SOUR:PRES:RANG \\\"100.00barg\\\";:SENS:PRES:RANG \\\"100.00barg\\\"\"\n\n[DRUCK-PACE6000]\nOpen = \"*RST\"\nMachineType = \"*CLS;*IDN?\"\nReadPressure = \"*CLS;SENS?\"\nSetPressure = \"*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON\"\nVent = \"*CLS;:Sour:Vent 1;:CAL:PRES:ZERP:VALV;*CLS;:OUTPUT OFF;*CLS;:OUTPUT ON\"\n\n[FLUKE-7250]\nOpen = \"*RST\"\nMachineType = \"*IDN?\"\nReadPressure = \"*CLS;MEAS?\"\nSetPressure = \"*CLS;UNIT KPa;:PRES 9999;:OUTP:MODE CONTROL\"\n\n[Keysight-DAQ973A]\nOpen = \"ROUT:OPEN (@9999)\"\nClose = \"ROUT:CLOS (@9999)\"\nSetVol = \"CONF:VOLT (@9999)\"\nReadValue = \"READ?\"\nReadTemp = \"READ?\"\n\n[Keysight-34970A]\nOpen = \"ROUT:OPEN (@9999)\"\nClose = \"ROUT:CLOS (@9999)\"\nSetVol = \"CONF:VOLT (@9999)\"\nReadValue = \"READ?\"\n\n[Keysight-34461]\nSetVol = \"CONF:VOLT\"\nReadValue = \"READ?\"";
	}

	private void LoadAppConfig()
	{
		try
		{
			IniFile iniFile = IniFile.Load(SettingPath);
			_planName.Text = iniFile.Get("Plan", "Name", _planName.Text);
			_csvPath.Text = iniFile.Get("Plan", "RawCsv", _csvPath.Text);
			_com.Text = iniFile.Get("Device.Board", "Address", _com.Text);
			_boardBaud.Text = iniFile.Get("Device.Board", "Baud", _boardBaud.Text);
			_boardDataBits.Text = iniFile.Get("Device.Board", "DataBits", _boardDataBits.Text);
			_boardParity.Text = iniFile.Get("Device.Board", "Parity", _boardParity.Text);
			_boardStopBits.Text = iniFile.Get("Device.Board", "StopBits", _boardStopBits.Text);
			_addr.Value = ClampDecimal(iniFile.GetInt("Device.Board", "Station", (int)_addr.Value), _addr.Minimum, _addr.Maximum);
			_boardSlotMap.Text = iniFile.Get("Device.Board", "SlotMap", _boardSlotMap.Text);
			_boardSlotMapDevice.Text = _boardSlotMap.Text;
			_boardSlotMapCal.Text = _boardSlotMap.Text;
			_useBoardChannel47.Checked = iniFile.GetBool("Device.Board", "UseChannel47", _useBoardChannel47.Checked);
			_useBoardChannel47Manual.Checked = _useBoardChannel47.Checked;
			_preCalBoardAddr.Value = _addr.Value;
			_timeout.Value = ClampDecimal(iniFile.GetInt("Device.Board", "TimeoutMs", (int)_timeout.Value), _timeout.Minimum, _timeout.Maximum);
			string text = iniFile.Get("Device.Oven", "Address", _ovenCom.Text);
			string text2 = iniFile.Get("Device.Oven", "Com", "");
			string text3 = iniFile.Get("Device.Oven", "Ip", "");
			if (string.IsNullOrWhiteSpace(text2) && IsSerialAddress(text))
			{
				text2 = text;
			}
			if (string.IsNullOrWhiteSpace(text3) && !IsSerialAddress(text))
			{
				text3 = text;
			}
			if (!string.IsNullOrWhiteSpace(text2))
			{
				_ovenCom.Text = text2;
			}
			if (!string.IsNullOrWhiteSpace(text3))
			{
				_ovenIp.Text = text3;
			}
			_ovenPort.Text = iniFile.Get("Device.Oven", "Port", _ovenPort.Text);
			_ovenUnitId.Text = ClampInt(iniFile.GetInt("Device.Oven", "UnitId", 0), 0, 255).ToString(CultureInfo.InvariantCulture);
			_ovenBaud.Text = iniFile.Get("Device.Oven", "Baud", _ovenBaud.Text);
			_ovenDataBits.Text = iniFile.Get("Device.Oven", "DataBits", _ovenDataBits.Text);
			_ovenParity.Text = iniFile.Get("Device.Oven", "Parity", _ovenParity.Text);
			_ovenStopBits.Text = iniFile.Get("Device.Oven", "StopBits", _ovenStopBits.Text);
			_pressureModel.Text = iniFile.Get("Device.Pressure", "Model", _pressureModel.Text);
			SetComboText(_pressureAddr, iniFile.Get("Device.Pressure", "Address", _pressureAddr.Text));
			(int, int) tuple = ParseGpib(_pressureAddr.Text);
			SetComboText(_pressureGpibPort, ClampInt(iniFile.GetInt("Device.Pressure", "GpibPort", tuple.Item1), 0, 9).ToString(CultureInfo.InvariantCulture));
			SetComboText(_pressureGpibAddress, ClampInt(iniFile.GetInt("Device.Pressure", "GpibAddress", tuple.Item2), 0, 30).ToString(CultureInfo.InvariantCulture));
			_dmmModel.Text = iniFile.Get("Device.Dmm", "Model", _dmmModel.Text);
			SetComboText(_dmmAddr, iniFile.Get("Device.Dmm", "Address", _dmmAddr.Text));
			(int, int) tuple2 = ParseGpib(_dmmAddr.Text);
			SetComboText(_dmmGpibPort, ClampInt(iniFile.GetInt("Device.Dmm", "GpibPort", tuple2.Item1), 0, 9).ToString(CultureInfo.InvariantCulture));
			SetComboText(_dmmGpibAddress, ClampInt(iniFile.GetInt("Device.Dmm", "GpibAddress", tuple2.Item2), 0, 30).ToString(CultureInfo.InvariantCulture));
			_useDaqChannel.Checked = iniFile.GetBool("DAQ", "UseChannel", _useDaqChannel.Checked);
			_multiDaq.Checked = iniFile.GetBool("DAQ", "MultiDaq", _multiDaq.Checked);
			_daqSkipChannel47.Checked = iniFile.GetBool("DAQ", "SkipChannel47", _daqSkipChannel47.Checked);
			_channelExpr.Text = iniFile.Get("DAQ", "DefaultMap", _channelExpr.Text);
			_daqChannelOverrideMap.Text = iniFile.Get("DAQ", "ManualChannelMap", _daqChannelOverrideMap.Text);
			List<string> list = new List<string>();
			for (int i = 0; i < 32; i++)
			{
				string text4 = iniFile.Get("DAQ", "Profile" + i, "");
				if (!string.IsNullOrWhiteSpace(text4))
				{
					list.Add(text4);
				}
			}
			if (list.Count > 0)
			{
				_daqProfiles.Text = string.Join(Environment.NewLine, list);
			}
			SyncDaqGridFromText();
			for (int j = 0; j < _daqCardChannels.Length; j++)
			{
				_daqCardChannels[j].Text = iniFile.Get("DAQ", "Card" + (j + 1), _daqCardChannels[j].Text);
			}
			_calSensorModel.Text = iniFile.Get("Calibration", "SensorModel", _calSensorModel.Text);
			_p0.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "P0", _p0.Value), _p0.Minimum, _p0.Maximum);
			_pmid.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "Pmid", _pmid.Value), _pmid.Minimum, _pmid.Maximum);
			_pfull.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "Pfull", _pfull.Value), _pfull.Minimum, _pfull.Maximum);
			_pressureUnit.Text = iniFile.Get("Calibration", "Unit", _pressureUnit.Text);
			_stableTolKpa.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "StableTolKpa", _stableTolKpa.Value), _stableTolKpa.Minimum, _stableTolKpa.Maximum);
			_stableSec.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "StableSec", _stableSec.Value), _stableSec.Minimum, _stableSec.Maximum);
			_settleSec.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "SettleSec", _settleSec.Value), _settleSec.Minimum, _settleSec.Maximum);
			_calOutputMinV.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "OutputMinV", _calOutputMinV.Value), _calOutputMinV.Minimum, _calOutputMinV.Maximum);
			_calOutputMaxV.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "OutputMaxV", _calOutputMaxV.Value), _calOutputMaxV.Minimum, _calOutputMaxV.Maximum);
			_calPercentMin.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "PercentMin", _calPercentMin.Value), _calPercentMin.Minimum, _calPercentMin.Maximum);
			_calPercentMax.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "PercentMax", _calPercentMax.Value), _calPercentMax.Minimum, _calPercentMax.Maximum);
			_calVoltageTolerance.Value = ClampDecimal(iniFile.GetDecimal("Calibration", "OutputTolV", _calVoltageTolerance.Value), _calVoltageTolerance.Minimum, _calVoltageTolerance.Maximum);
			_calMaxRetryCount.Value = ClampDecimal(iniFile.GetInt("Calibration", "MaxRetryCount", (int)_calMaxRetryCount.Value), _calMaxRetryCount.Minimum, _calMaxRetryCount.Maximum);
			_preserveTempCoe.Checked = true;
			_writeBoard.Checked = iniFile.GetBool("Calibration", "WriteBoard", _writeBoard.Checked);
			_verifyAfterWrite.Checked = iniFile.GetBool("Calibration", "VerifyAfterWrite", _verifyAfterWrite.Checked);
			_calLinearityEnabled.Checked = iniFile.GetBool("Calibration", "LinearityEnabled", _calLinearityEnabled.Checked);
			_batchPressureMode.Checked = iniFile.GetBool("Calibration", "BatchPressureMode", _batchPressureMode.Checked);
			_writeConfigBeforeCal.Checked = iniFile.GetBool("Calibration", "WriteConfigBeforeCal", _writeConfigBeforeCal.Checked);
			_preCalConfigGroup.Text = iniFile.Get("Calibration", "PreCalConfigGroup", _preCalConfigGroup.Text);
			_preCalRegAHex.Text = iniFile.Get("Calibration", "PreCalRegAHex", _preCalRegAHex.Text);
			_preCalRegBHex.Text = iniFile.Get("Calibration", "PreCalRegBHex", _preCalRegBHex.Text);
			string text5 = iniFile.Get("Calibration", "PreCalConfigHex", "");
			if (!string.IsNullOrWhiteSpace(text5) && string.IsNullOrWhiteSpace(iniFile.Get("Calibration", "PreCalRegAHex", "")))
			{
				byte[] array = ParseHex(text5);
				if (array.Length >= 4)
				{
					_preCalRegAHex.Text = string.Concat(from b in array.Take(2)
						select b.ToString("X2"));
					_preCalRegBHex.Text = string.Concat(from b in array.Skip(2).Take(2)
						select b.ToString("X2"));
				}
			}
			_preCalStartSlot.Value = ClampDecimal(iniFile.GetInt("Calibration", "PreCalStartSlot", (int)_preCalStartSlot.Value), _preCalStartSlot.Minimum, _preCalStartSlot.Maximum);
			_preCalConfigCount.Value = ClampDecimal(iniFile.GetInt("Calibration", "PreCalConfigCount", (int)_preCalConfigCount.Value), _preCalConfigCount.Minimum, _preCalConfigCount.Maximum);
			_compSensorModel.Text = iniFile.Get("Compensation", "SensorModel", _compSensorModel.Text);
			_compUseOven.Checked = iniFile.GetBool("Compensation", "UseOven", _compUseOven.Checked);
			_compUseDebug.Checked = iniFile.GetBool("Compensation", "UseDebug", _compUseDebug.Checked);
			_compAutoConfig.Checked = iniFile.GetBool("Compensation", "AutoConfig", _compAutoConfig.Checked);
			_compTest.Checked = iniFile.GetBool("Compensation", "TestAfterWrite", _compTest.Checked);
			_compWriteNumber.Checked = iniFile.GetBool("Compensation", "WriteNumber", _compWriteNumber.Checked);
			_compWritePreConfig.Checked = iniFile.GetBool("Compensation", "WritePreConfig", _compWritePreConfig.Checked);
			_compWriteCoefficients.Checked = iniFile.GetBool("Compensation", "WriteCoefficients", _compWriteCoefficients.Checked);
			_compOvenModel.Text = iniFile.Get("Compensation", "OvenModel", _compOvenModel.Text);
			_compStartSlot.Value = ClampDecimal(iniFile.GetInt("Compensation", "StartSlot", (int)_compStartSlot.Value), _compStartSlot.Minimum, _compStartSlot.Maximum);
			_compSlotCount.Value = ClampDecimal(iniFile.GetInt("Compensation", "SlotCount", (int)_compSlotCount.Value), _compSlotCount.Minimum, _compSlotCount.Maximum);
			_compP0.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "P0", _compP0.Value), _compP0.Minimum, _compP0.Maximum);
			_compP50.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "P50", _compP50.Value), _compP50.Minimum, _compP50.Maximum);
			_compP100.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "P100", _compP100.Value), _compP100.Minimum, _compP100.Maximum);
			_compPressureUnit.Text = iniFile.Get("Compensation", "PressureUnit", _compPressureUnit.Text);
			_compT1.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "T1", _compT1.Value), _compT1.Minimum, _compT1.Maximum);
			_compT2.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "T2", _compT2.Value), _compT2.Minimum, _compT2.Maximum);
			_compT3.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "T3", _compT3.Value), _compT3.Minimum, _compT3.Maximum);
			_compTempTol.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "TempTol", _compTempTol.Value), _compTempTol.Minimum, _compTempTol.Maximum);
			_compTempHoldSec.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "TempHoldSec", _compTempHoldSec.Value), _compTempHoldSec.Minimum, _compTempHoldSec.Maximum);
			_compPressureHoldSec.Value = ClampDecimal(iniFile.GetDecimal("Compensation", "PressureHoldSec", _compPressureHoldSec.Value), _compPressureHoldSec.Minimum, _compPressureHoldSec.Maximum);
			_compOutputDir.Text = iniFile.Get("Compensation", "OutputDir", _compOutputDir.Text);
			_testSensorModel.Text = iniFile.Get("F40Test", "SensorModel", _testSensorModel.Text);
			_testStartSlot.Value = ClampDecimal(iniFile.GetInt("F40Test", "StartSlot", (int)_testStartSlot.Value), _testStartSlot.Minimum, _testStartSlot.Maximum);
			_testSlotCount.Value = ClampDecimal(iniFile.GetInt("F40Test", "SlotCount", (int)_testSlotCount.Value), _testSlotCount.Minimum, _testSlotCount.Maximum);
			_testUsePressure.Checked = iniFile.GetBool("F40Test", "UsePressure", _testUsePressure.Checked);
			_testUseOven.Checked = iniFile.GetBool("F40Test", "UseOven", _testUseOven.Checked);
			_testUseDmm.Checked = iniFile.GetBool("F40Test", "UseDmm", _testUseDmm.Checked);
			_testTempHoldSec.Value = ClampDecimal(iniFile.GetDecimal("F40Test", "TempHoldSec", _testTempHoldSec.Value), _testTempHoldSec.Minimum, _testTempHoldSec.Maximum);
			_testPressureHoldSec.Value = ClampDecimal(iniFile.GetDecimal("F40Test", "PressureHoldSec", _testPressureHoldSec.Value), _testPressureHoldSec.Minimum, _testPressureHoldSec.Maximum);
			_testVoltageTolerance.Value = ClampDecimal(iniFile.GetDecimal("F40Test", "VoltageTolerance", _testVoltageTolerance.Value), _testVoltageTolerance.Minimum, _testVoltageTolerance.Maximum);
			_testOutputDir.Text = iniFile.Get("F40Test", "OutputDir", _testOutputDir.Text);
			RefreshCompensationSlotGrid(showLog: false);
			RefreshF40TestSlotGrid(showLog: false);
			LoadF40TestPlanSafe(writeLog: false);
			SyncDeviceGridFromControls();
		}
		catch (Exception ex)
		{
			Log("加载Setting.ini失败：" + ex.Message, important: true);
		}
	}

	private void SaveAppConfig()
	{
		try
		{
			Directory.CreateDirectory(SettingDir);
			SyncGpibComboFromAddress(_pressureAddr, _pressureGpibPort, _pressureGpibAddress);
			SyncGpibComboFromAddress(_dmmAddr, _dmmGpibPort, _dmmGpibAddress);
			StringBuilder sb = new StringBuilder();
			Sec("Plan");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Name = ");
			handler.AppendFormatted(Q(_planName.Text));
			stringBuilder2.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
			handler.AppendLiteral("RawCsv = ");
			handler.AppendFormatted(Q(_csvPath.Text));
			stringBuilder3.AppendLine(ref handler);
			Sec("Device.Board");
			sb.AppendLine("Model = \"Board\"");
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("Address = ");
			handler.AppendFormatted(Q(_com.Text));
			stringBuilder4.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Baud = ");
			handler.AppendFormatted(Q(_boardBaud.Text));
			stringBuilder5.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder6 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("DataBits = ");
			handler.AppendFormatted(Q(_boardDataBits.Text));
			stringBuilder6.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder7 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
			handler.AppendLiteral("Parity = ");
			handler.AppendFormatted(Q(_boardParity.Text));
			stringBuilder7.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder8 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("StopBits = ");
			handler.AppendFormatted(Q(_boardStopBits.Text));
			stringBuilder8.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder9 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("Station = ");
			handler.AppendFormatted(Q(_addr.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder9.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder10 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("SlotMap = ");
			handler.AppendFormatted(Q(_boardSlotMap.Text));
			stringBuilder10.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder11 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
			handler.AppendLiteral("UseChannel47 = ");
			handler.AppendFormatted(Q(_useBoardChannel47.Checked ? "TRUE" : "FALSE"));
			stringBuilder11.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder12 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("TimeoutMs = ");
			handler.AppendFormatted(Q(_timeout.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder12.AppendLine(ref handler);
			Sec("Device.Oven");
			sb.AppendLine($"Model = {Q(_compOvenModel.Text)}");
			stringBuilder = sb;
			StringBuilder stringBuilder13 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("Address = ");
			handler.AppendFormatted(Q(GetOvenPrimaryAddress()));
			stringBuilder13.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder14 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("Ip = ");
			handler.AppendFormatted(Q(_ovenIp.Text));
			stringBuilder14.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder15 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Port = ");
			handler.AppendFormatted(Q(_ovenPort.Text));
			stringBuilder15.AppendLine(ref handler);
			sb.AppendLine($"UnitId = {Q(_ovenUnitId.Text)}");
			stringBuilder = sb;
			StringBuilder stringBuilder16 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder);
			handler.AppendLiteral("Com = ");
			handler.AppendFormatted(Q(_ovenCom.Text));
			stringBuilder16.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder17 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Baud = ");
			handler.AppendFormatted(Q(_ovenBaud.Text));
			stringBuilder17.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder18 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("DataBits = ");
			handler.AppendFormatted(Q(_ovenDataBits.Text));
			stringBuilder18.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder19 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
			handler.AppendLiteral("Parity = ");
			handler.AppendFormatted(Q(_ovenParity.Text));
			stringBuilder19.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder20 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("StopBits = ");
			handler.AppendFormatted(Q(_ovenStopBits.Text));
			stringBuilder20.AppendLine(ref handler);
			Sec("Device.Pressure");
			stringBuilder = sb;
			StringBuilder stringBuilder21 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("Model = ");
			handler.AppendFormatted(Q(_pressureModel.Text));
			stringBuilder21.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder22 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("Address = ");
			handler.AppendFormatted(Q(_pressureAddr.Text));
			stringBuilder22.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder23 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("GpibPort = ");
			handler.AppendFormatted(Q(ComboInt(_pressureGpibPort, 0, 0, 9).ToString(CultureInfo.InvariantCulture)));
			stringBuilder23.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder24 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("GpibAddress = ");
			handler.AppendFormatted(Q(ComboInt(_pressureGpibAddress, 8, 0, 30).ToString(CultureInfo.InvariantCulture)));
			stringBuilder24.AppendLine(ref handler);
			sb.AppendLine("Mode = \"Hw\"");
			Sec("Device.Dmm");
			stringBuilder = sb;
			StringBuilder stringBuilder25 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("Model = ");
			handler.AppendFormatted(Q(_dmmModel.Text));
			stringBuilder25.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder26 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("Address = ");
			handler.AppendFormatted(Q(_dmmAddr.Text));
			stringBuilder26.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder27 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("GpibPort = ");
			handler.AppendFormatted(Q(ComboInt(_dmmGpibPort, 0, 0, 9).ToString(CultureInfo.InvariantCulture)));
			stringBuilder27.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder28 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("GpibAddress = ");
			handler.AppendFormatted(Q(ComboInt(_dmmGpibAddress, 22, 0, 30).ToString(CultureInfo.InvariantCulture)));
			stringBuilder28.AppendLine(ref handler);
			sb.AppendLine("Mode = \"Hw\"");
			SyncDaqProfilesTextFromGrid();
			Sec("DAQ");
			stringBuilder = sb;
			StringBuilder stringBuilder29 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("UseChannel = ");
			handler.AppendFormatted(Q(_useDaqChannel.Checked ? "TRUE" : "FALSE"));
			stringBuilder29.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder30 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("MultiDaq = ");
			handler.AppendFormatted(Q(_multiDaq.Checked ? "TRUE" : "FALSE"));
			stringBuilder30.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder31 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
			handler.AppendLiteral("SkipChannel47 = ");
			handler.AppendFormatted(Q(_daqSkipChannel47.Checked ? "TRUE" : "FALSE"));
			stringBuilder31.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder32 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("DefaultMap = ");
			handler.AppendFormatted(Q(_channelExpr.Text));
			stringBuilder32.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilderManualChannelMap = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
			handler.AppendLiteral("ManualChannelMap = ");
			handler.AppendFormatted(Q(_daqChannelOverrideMap.Text));
			stringBuilderManualChannelMap.AppendLine(ref handler);
			List<string> list = (from x in _daqProfiles.Text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
				select x.Trim() into x
				where x.Length > 0
				select x).ToList();
			for (int num = 0; num < list.Count; num++)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder33 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(10, 2, stringBuilder);
				handler.AppendLiteral("Profile");
				handler.AppendFormatted(num);
				handler.AppendLiteral(" = ");
				handler.AppendFormatted(Q(list[num]));
				stringBuilder33.AppendLine(ref handler);
			}
			for (int num2 = 0; num2 < _daqCardChannels.Length; num2++)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder34 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(7, 2, stringBuilder);
				handler.AppendLiteral("Card");
				handler.AppendFormatted(num2 + 1);
				handler.AppendLiteral(" = ");
				handler.AppendFormatted(Q(_daqCardChannels[num2].Text));
				stringBuilder34.AppendLine(ref handler);
			}
			Sec("Calibration");
			stringBuilder = sb;
			StringBuilder stringBuilder35 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("SensorModel = ");
			handler.AppendFormatted(Q(_calSensorModel.Text));
			stringBuilder35.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder36 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("P0 = ");
			handler.AppendFormatted(Q(_p0.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder36.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder37 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Pmid = ");
			handler.AppendFormatted(Q(_pmid.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder37.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder38 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("Pfull = ");
			handler.AppendFormatted(Q(_pfull.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder38.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder39 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Unit = ");
			handler.AppendFormatted(Q(_pressureUnit.Text));
			stringBuilder39.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder40 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
			handler.AppendLiteral("StableTolKpa = ");
			handler.AppendFormatted(Q(_stableTolKpa.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder40.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder41 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("StableSec = ");
			handler.AppendFormatted(Q(_stableSec.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder41.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder42 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("SettleSec = ");
			handler.AppendFormatted(Q(_settleSec.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder42.AppendLine(ref handler);
			sb.AppendLine("OutputMinV = " + Q(_calOutputMinV.Value.ToString(CultureInfo.InvariantCulture)));
			sb.AppendLine("OutputMaxV = " + Q(_calOutputMaxV.Value.ToString(CultureInfo.InvariantCulture)));
			sb.AppendLine("PercentMin = " + Q(_calPercentMin.Value.ToString(CultureInfo.InvariantCulture)));
			sb.AppendLine("PercentMax = " + Q(_calPercentMax.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder = sb;
			StringBuilder stringBuilder43 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("OutputTolV = ");
			handler.AppendFormatted(Q(_calVoltageTolerance.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder43.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder44 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
			handler.AppendLiteral("MaxRetryCount = ");
			handler.AppendFormatted(Q(_calMaxRetryCount.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder44.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder45 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder);
			handler.AppendLiteral("PreserveTempCoefficients = ");
			handler.AppendFormatted(Q(_preserveTempCoe.Checked ? "TRUE" : "FALSE"));
			stringBuilder45.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder46 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("WriteBoard = ");
			handler.AppendFormatted(Q(_writeBoard.Checked ? "TRUE" : "FALSE"));
			stringBuilder46.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder47 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
			handler.AppendLiteral("VerifyAfterWrite = ");
			handler.AppendFormatted(Q(_verifyAfterWrite.Checked ? "TRUE" : "FALSE"));
			stringBuilder47.AppendLine(ref handler);
			sb.AppendLine("LinearityEnabled = " + Q(_calLinearityEnabled.Checked ? "TRUE" : "FALSE"));
			stringBuilder = sb;
			StringBuilder stringBuilder48 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
			handler.AppendLiteral("BatchPressureMode = ");
			handler.AppendFormatted(Q(_batchPressureMode.Checked ? "TRUE" : "FALSE"));
			stringBuilder48.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder49 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
			handler.AppendLiteral("WriteConfigBeforeCal = ");
			handler.AppendFormatted(Q(_writeConfigBeforeCal.Checked ? "TRUE" : "FALSE"));
			stringBuilder49.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder50 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
			handler.AppendLiteral("PreCalConfigGroup = ");
			handler.AppendFormatted(Q(_preCalConfigGroup.Text));
			stringBuilder50.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder51 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
			handler.AppendLiteral("PreCalRegAHex = ");
			handler.AppendFormatted(Q(_preCalRegAHex.Text));
			stringBuilder51.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder52 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
			handler.AppendLiteral("PreCalRegBHex = ");
			handler.AppendFormatted(Q(_preCalRegBHex.Text));
			stringBuilder52.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder53 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder);
			handler.AppendLiteral("PreCalStartSlot = ");
			handler.AppendFormatted(Q(_preCalStartSlot.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder53.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder54 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
			handler.AppendLiteral("PreCalConfigCount = ");
			handler.AppendFormatted(Q(_preCalConfigCount.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder54.AppendLine(ref handler);
			Sec("Compensation");
			stringBuilder = sb;
			StringBuilder stringBuilder55 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("SensorModel = ");
			handler.AppendFormatted(Q(_compSensorModel.Text));
			stringBuilder55.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder56 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("UseOven = ");
			handler.AppendFormatted(Q(_compUseOven.Checked ? "TRUE" : "FALSE"));
			stringBuilder56.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder57 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("UseDebug = ");
			handler.AppendFormatted(Q(_compUseDebug.Checked ? "TRUE" : "FALSE"));
			stringBuilder57.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder58 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("AutoConfig = ");
			handler.AppendFormatted(Q(_compAutoConfig.Checked ? "TRUE" : "FALSE"));
			stringBuilder58.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder59 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
			handler.AppendLiteral("TestAfterWrite = ");
			handler.AppendFormatted(Q(_compTest.Checked ? "TRUE" : "FALSE"));
			stringBuilder59.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder60 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("WriteNumber = ");
			handler.AppendFormatted(Q(_compWriteNumber.Checked ? "TRUE" : "FALSE"));
			stringBuilder60.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder61 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
			handler.AppendLiteral("WritePreConfig = ");
			handler.AppendFormatted(Q(_compWritePreConfig.Checked ? "TRUE" : "FALSE"));
			stringBuilder61.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder62 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
			handler.AppendLiteral("WriteCoefficients = ");
			handler.AppendFormatted(Q(_compWriteCoefficients.Checked ? "TRUE" : "FALSE"));
			stringBuilder62.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder63 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("OvenModel = ");
			handler.AppendFormatted(Q(_compOvenModel.Text));
			stringBuilder63.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder64 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("StartSlot = ");
			handler.AppendFormatted(Q(_compStartSlot.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder64.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder65 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("SlotCount = ");
			handler.AppendFormatted(Q(_compSlotCount.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder65.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder66 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("P0 = ");
			handler.AppendFormatted(Q(_compP0.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder66.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder67 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder);
			handler.AppendLiteral("P50 = ");
			handler.AppendFormatted(Q(_compP50.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder67.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder68 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("P100 = ");
			handler.AppendFormatted(Q(_compP100.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder68.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder69 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
			handler.AppendLiteral("PressureUnit = ");
			handler.AppendFormatted(Q(_compPressureUnit.Text));
			stringBuilder69.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder70 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("T1 = ");
			handler.AppendFormatted(Q(_compT1.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder70.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder71 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("T2 = ");
			handler.AppendFormatted(Q(_compT2.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder71.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder72 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
			handler.AppendLiteral("T3 = ");
			handler.AppendFormatted(Q(_compT3.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder72.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder73 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("TempTol = ");
			handler.AppendFormatted(Q(_compTempTol.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder73.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder74 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("TempHoldSec = ");
			handler.AppendFormatted(Q(_compTempHoldSec.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder74.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder75 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder);
			handler.AppendLiteral("PressureHoldSec = ");
			handler.AppendFormatted(Q(_compPressureHoldSec.Value.ToString(CultureInfo.InvariantCulture)));
			stringBuilder75.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder76 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
			handler.AppendLiteral("OutputDir = ");
			handler.AppendFormatted(Q(_compOutputDir.Text));
			stringBuilder76.AppendLine(ref handler);
			Sec("F40Test");
			sb.AppendLine($"SensorModel = {Q(_testSensorModel.Text)}");
			sb.AppendLine($"StartSlot = {Q(_testStartSlot.Value.ToString(CultureInfo.InvariantCulture))}");
			sb.AppendLine($"SlotCount = {Q(_testSlotCount.Value.ToString(CultureInfo.InvariantCulture))}");
			sb.AppendLine($"UsePressure = {Q(_testUsePressure.Checked ? "TRUE" : "FALSE")}");
			sb.AppendLine($"UseOven = {Q(_testUseOven.Checked ? "TRUE" : "FALSE")}");
			sb.AppendLine($"UseDmm = {Q(_testUseDmm.Checked ? "TRUE" : "FALSE")}");
			sb.AppendLine($"TempHoldSec = {Q(_testTempHoldSec.Value.ToString(CultureInfo.InvariantCulture))}");
			sb.AppendLine($"PressureHoldSec = {Q(_testPressureHoldSec.Value.ToString(CultureInfo.InvariantCulture))}");
			sb.AppendLine($"VoltageTolerance = {Q(_testVoltageTolerance.Value.ToString(CultureInfo.InvariantCulture))}");
			sb.AppendLine($"OutputDir = {Q(_testOutputDir.Text)}");
			File.WriteAllText(SettingPath, sb.ToString().TrimStart(), Encoding.UTF8);
			SaveCommandFile();
			SyncDeviceGridFromControls();
			Log("配置已保存：" + SettingPath + " / " + CommandPath, important: true);
			void Sec(string s)
			{
				sb.AppendLine().Append('[').Append(s)
					.AppendLine("]");
			}
		}
		catch (Exception ex)
		{
			Log("保存配置失败：" + ex.Message, important: true);
		}
		static string Q(string s)
		{
			return "\"" + s.Replace("\"", "\\\"") + "\"";
		}
	}

	private static decimal ClampDecimal(decimal v, decimal min, decimal max)
	{
		return Math.Min(max, Math.Max(min, v));
	}

	private static int ClampInt(int v, int min, int max)
	{
		return Math.Min(max, Math.Max(min, v));
	}

	private static bool IsSerialAddress(string? value)
	{
		return !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase);
	}

	private static (int port, int addr) ParseGpib(string address)
	{
		Match match = Regex.Match(address ?? "", "GPIB(\\d+)::(\\d+)::", RegexOptions.IgnoreCase);
		return match.Success ? (port: int.Parse(match.Groups[1].Value), addr: int.Parse(match.Groups[2].Value)) : (port: 0, addr: 0);
	}

	private bool IsTcpOvenModel(string? model)
	{
		return string.Equals((model ?? "").Trim(), "SIDAUMC1000", StringComparison.OrdinalIgnoreCase);
	}

	private string GetOvenPrimaryAddress()
	{
		return IsTcpOvenModel(_compOvenModel.Text) ? _ovenIp.Text.Trim() : _ovenCom.Text.Trim();
	}

	private string GetOvenEndpointText(string? model = null)
	{
		if (model == null)
		{
			model = _compOvenModel.Text;
		}
		return IsTcpOvenModel(model) ? (_ovenIp.Text.Trim() + ":" + (string.IsNullOrWhiteSpace(_ovenPort.Text) ? "508" : _ovenPort.Text.Trim())) : _ovenCom.Text.Trim();
	}

	private bool HasOvenEndpoint()
	{
		if (IsTcpOvenModel(_compOvenModel.Text))
		{
			return !string.IsNullOrWhiteSpace(_ovenIp.Text) && !string.IsNullOrWhiteSpace(_ovenPort.Text);
		}
		return !string.IsNullOrWhiteSpace(_ovenCom.Text);
	}

	private static int ComboInt(ComboBox cb, int fallback, int min, int max)
	{
		int result;
		return int.TryParse(cb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? ClampInt(result, min, max) : fallback;
	}

	private static void SetComboText(ComboBox cb, string value)
	{
		value = value.Trim();
		if (value.Length != 0)
		{
			if (!cb.Items.Cast<object>().Any((object x) => string.Equals(Convert.ToString(x), value, StringComparison.OrdinalIgnoreCase)))
			{
				cb.Items.Add(value);
			}
			cb.Text = value;
		}
	}

	private void UpdateAddressesFromGpibNumeric()
	{
		if (_syncGpibBusy)
		{
			return;
		}
		_syncGpibBusy = true;
		try
		{
			SetComboText(_pressureAddr, $"GPIB{ComboInt(_pressureGpibPort, 0, 0, 9)}::{ComboInt(_pressureGpibAddress, 8, 0, 30)}::INSTR");
			SetComboText(_dmmAddr, $"GPIB{ComboInt(_dmmGpibPort, 0, 0, 9)}::{ComboInt(_dmmGpibAddress, 22, 0, 30)}::INSTR");
			RefreshVisaResources();
		}
		finally
		{
			_syncGpibBusy = false;
		}
	}

	private void SyncGpibComboFromAddress(ComboBox addressBox, ComboBox portBox, ComboBox addrBox)
	{
		if (_syncGpibBusy)
		{
			return;
		}
		(int, int) tuple = ParseGpib(addressBox.Text);
		if (tuple.Item1 < 0 || tuple.Item1 > 9 || tuple.Item2 < 0 || tuple.Item2 > 30)
		{
			return;
		}
		_syncGpibBusy = true;
		try
		{
			SetComboText(portBox, tuple.Item1.ToString(CultureInfo.InvariantCulture));
			SetComboText(addrBox, tuple.Item2.ToString(CultureInfo.InvariantCulture));
		}
		finally
		{
			_syncGpibBusy = false;
		}
	}

	private void ApplyCalibrationModelPressure(bool writeLog)
	{
		string text = _calSensorModel.Text.Trim();
		CalibrationPlan plan = BuildCalibrationPlan(text);
		ApplyCalibrationPlan(plan, writeLog);
	}

	private CalibrationPlan BuildCalibrationPlan(string model)
	{
		model = string.IsNullOrWhiteSpace(model) ? "F40_100psi" : model.Trim();
		foreach (string path in ResolveCalibrationConfigCandidates(model))
		{
			IniFile ini = IniFile.Load(path);
			string unit = NormalizePressureUnit(FindIniValueContains(ini, "压力单位") ?? DetectCompPressureUnit(model, "psi"));
			List<double> points = ReadCalibrationPressureValues(ini, "压力.压力点", unit);
			if (points.Count < 2)
			{
				continue;
			}
			string planModel = FindIniValue(ini, "测试名") ?? Path.GetFileNameWithoutExtension(path);
			double outputMin = FindIniDoubleContains(ini, 0.5, "AoutMin");
			double outputMax = FindIniDoubleContains(ini, 4.5, "AoutMax");
			double percentMin = FindIniDoubleContains(ini, 10.0, "百分比Min");
			double percentMax = FindIniDoubleContains(ini, 90.0, "百分比Max");
			double dacTolerance = FindIniDoubleContains(ini, 0.008, "DAC校准精度");
			bool linearity = ParseIniBool(FindIniValueContains(ini, "线性"), points.Count >= 3);
			if (points.Count < 3)
			{
				linearity = false;
			}
			return new CalibrationPlan(planModel, points, unit, outputMin, outputMax, percentMin, percentMax, dacTolerance, linearity, path);
		}
		return BuildFallbackCalibrationPlan(model);
	}

	private CalibrationPlan BuildFallbackCalibrationPlan(string model)
	{
		string unit = "psi";
		double full = 100.0;
		Match psi = Regex.Match(model, "(?<v>\\d+(?:\\.\\d+)?)\\s*psi", RegexOptions.IgnoreCase);
		Match kpa = Regex.Match(model, "(?<v>\\d+(?:\\.\\d+)?)\\s*kpa", RegexOptions.IgnoreCase);
		Match mpa = Regex.Match(model, "(?<v>\\d+(?:\\.\\d+)?)\\s*mpa", RegexOptions.IgnoreCase);
		if (psi.Success)
		{
			full = double.Parse(psi.Groups["v"].Value, CultureInfo.InvariantCulture);
			unit = "psi";
		}
		else if (kpa.Success)
		{
			full = double.Parse(kpa.Groups["v"].Value, CultureInfo.InvariantCulture);
			unit = "kPa";
		}
		else if (mpa.Success)
		{
			full = double.Parse(mpa.Groups["v"].Value, CultureInfo.InvariantCulture) * 1000.0;
			unit = "kPa";
		}
		else if (model.Equals("A1m", StringComparison.OrdinalIgnoreCase) || model.Contains("1m", StringComparison.OrdinalIgnoreCase))
		{
			full = 1000.0;
			unit = "kPa";
		}
		List<double> points = new List<double> { 0.0, full / 2.0, full };
		return new CalibrationPlan(string.IsNullOrWhiteSpace(model) ? "F40_100psi" : model, points, unit, CalibrationTargetMinV, CalibrationTargetMaxV, CalibrationTargetPercentMin, CalibrationTargetPercentMax, CalibrationOutputToleranceV, true, "型号推断 fallback");
	}

	private IEnumerable<string> ResolveCalibrationConfigCandidates(string model)
	{
		List<string> names = new List<string>();
		if (!string.IsNullOrWhiteSpace(model))
		{
			string trimmed = model.Trim();
			names.Add(trimmed + ".ini");
			if (trimmed.StartsWith("F40_", StringComparison.OrdinalIgnoreCase))
			{
				names.Add(trimmed.Substring(4) + ".ini");
			}
			if (trimmed.Equals("F40_150psi", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("150psi.ini");
			}
			if (trimmed.Equals("F40_15psi", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("15psi.ini");
			}
			if (trimmed.Equals("F40_30psi", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("30psi.ini");
			}
			if (trimmed.Equals("F40_250psi", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("250psi.ini");
			}
			if (trimmed.Equals("F40_1MPa", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("A1m", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("1MPa.ini");
			}
			if (trimmed.Equals("F40_0.6MPa", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("F40_600KPa", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("F40_600K", StringComparison.OrdinalIgnoreCase))
			{
				names.Add("F40_600K.ini");
			}
			if (trimmed.IndexOf("-7", StringComparison.OrdinalIgnoreCase) >= 0 || trimmed.IndexOf("+7", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				names.Add("-7kpa-----7kpa.ini");
			}
		}
		IEnumerable<string> dirs = new string[]
		{
			Path.Combine(SettingDir, "CalibrationTestConfig"),
			Path.Combine(AppContext.BaseDirectory, "setting", "CalibrationTestConfig"),
			Path.Combine(Environment.CurrentDirectory, "setting", "CalibrationTestConfig"),
			"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\F40标定\\setting\\TestConfig"
		}.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
		HashSet<string> yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string dir in dirs)
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				string path = Path.Combine(dir, name);
				if (File.Exists(path) && yielded.Add(path))
				{
					yield return path;
				}
			}
			if (string.IsNullOrWhiteSpace(model))
			{
				continue;
			}
			foreach (string path in Directory.GetFiles(dir, "*.ini", SearchOption.TopDirectoryOnly).Where((string x) => string.Equals(Path.GetFileNameWithoutExtension(x), model.Trim(), StringComparison.OrdinalIgnoreCase)))
			{
				if (yielded.Add(path))
				{
					yield return path;
				}
			}
		}
	}

	private static List<double> ReadCalibrationPressureValues(IniFile ini, string keyPrefix, string defaultUnit)
	{
		int limit = int.MaxValue;
		string? sizeText = FindIniValue(ini, keyPrefix + ".<size(s)>");
		if (sizeText != null && TryParseNumberFromText(sizeText, out double sizeValue))
		{
			limit = Math.Max(0, (int)Math.Round(sizeValue));
		}
		List<double> result = new List<double>();
		foreach ((int Index, string Value) item in ReadIndexedIniValues(ini, keyPrefix).OrderBy((x) => x.Index))
		{
			if (item.Index >= limit)
			{
				continue;
			}
			if (TryParseNumberFromText(item.Value, out double value))
			{
				string fromUnit = DetectCompPressureUnit(item.Value, defaultUnit);
				result.Add(ConvertCompPressureUnit(value, fromUnit, defaultUnit));
			}
		}
		return result;
	}

	private void ApplyCalibrationPlan(CalibrationPlan plan, bool writeLog)
	{
		_p0.Value = ClampDecimal((decimal)plan.P0, _p0.Minimum, _p0.Maximum);
		_pfull.Value = ClampDecimal((decimal)plan.Pfull, _pfull.Minimum, _pfull.Maximum);
		_pmid.Value = ClampDecimal((decimal)plan.Pmid, _pmid.Minimum, _pmid.Maximum);
		_pressureUnit.Text = NormalizePressureUnit(plan.PressureUnit);
		_calOutputMinV.Value = ClampDecimal((decimal)plan.OutputMinV, _calOutputMinV.Minimum, _calOutputMinV.Maximum);
		_calOutputMaxV.Value = ClampDecimal((decimal)plan.OutputMaxV, _calOutputMaxV.Minimum, _calOutputMaxV.Maximum);
		_calPercentMin.Value = ClampDecimal((decimal)plan.PercentMin, _calPercentMin.Minimum, _calPercentMin.Maximum);
		_calPercentMax.Value = ClampDecimal((decimal)plan.PercentMax, _calPercentMax.Minimum, _calPercentMax.Maximum);
		_calVoltageTolerance.Value = ClampDecimal((decimal)Math.Max(0.0, plan.DacToleranceV), _calVoltageTolerance.Minimum, _calVoltageTolerance.Maximum);
		_calLinearityEnabled.Checked = plan.LinearityEnabled;
		string modelText = string.IsNullOrWhiteSpace(_calSensorModel.Text) ? plan.Model : _calSensorModel.Text.Trim();
		_planName.Text = modelText + "-MultiDAQ";
		ApplyCalibrationTargetsToRows(resetDesiredPercents: true);
		if (writeLog)
		{
			string source = File.Exists(plan.Source) ? Path.GetFileName(plan.Source) : plan.Source;
			Log($"已应用F40标定方案：{plan.Model}，压力点={string.Join("/", plan.PressurePoints.Select((double x) => x.ToString("0.###", CultureInfo.InvariantCulture)))} {NormalizePressureUnit(plan.PressureUnit)}，输出={FormatVoltageTarget(plan.OutputMinV)}/{FormatVoltageTarget(plan.OutputMaxV)}，百分比={plan.PercentMin:0.##}%/{plan.PercentMax:0.##}%，容差=±{plan.DacToleranceV:0.###}V，线性={(plan.LinearityEnabled ? "启用" : "关闭")}，来源={source}", important: true);
		}
	}

	private void ApplyCalibrationTargetsToRows(bool resetDesiredPercents)
	{
		foreach (F40SlotRow row in _rows)
		{
			row.ApplyTargetDefinitions(CalibrationTargetMinV, CalibrationTargetMaxV, CalibrationTargetPercentMin, CalibrationTargetPercentMax, resetDesiredPercents);
		}
		_grid.Refresh();
	}

	private static bool ParseIniBool(string? value, bool fallback)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}
		string text = value.Trim().Trim('"');
		if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("YES", StringComparison.OrdinalIgnoreCase) || text == "1")
		{
			return true;
		}
		if (text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || text.Equals("NO", StringComparison.OrdinalIgnoreCase) || text == "0")
		{
			return false;
		}
		return fallback;
	}

	private static string FormatVoltageTarget(double value)
	{
		return value.ToString("0.###", CultureInfo.InvariantCulture) + "V";
	}

	private void InferCalibrationModelFromCsvPath(string path)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
		Match match = Regex.Match(fileNameWithoutExtension, "F40[_\\-]?(?<range>\\d+(?:\\.\\d+)?\\s*(?:psi|kPa|MPa))", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			string value = "F40_" + match.Groups["range"].Value.Replace(" ", "");
			SetComboText(_calSensorModel, value);
			ApplyCalibrationModelPressure(writeLog: true);
		}
	}

	private void SetupDaqProfileGrid()
	{
		_daqProfileGrid.Columns.Clear();
		_daqProfileGrid.Columns.Add(new DataGridViewCheckBoxColumn
		{
			Name = "Enabled",
			HeaderText = "启用",
			Width = 55,
			FillWeight = 45f
		});
		_daqProfileGrid.Columns.Add(new DataGridViewTextBoxColumn
		{
			Name = "From",
			HeaderText = "起始工位",
			Width = 85,
			FillWeight = 70f
		});
		_daqProfileGrid.Columns.Add(new DataGridViewTextBoxColumn
		{
			Name = "To",
			HeaderText = "结束工位",
			Width = 85,
			FillWeight = 70f
		});
		_daqProfileGrid.Columns.Add(new DataGridViewComboBoxColumn
		{
			Name = "Visa",
			HeaderText = "DAQ973A VISA地址（下拉选择/自动扫描）",
			Width = 260,
			FillWeight = 190f,
			FlatStyle = FlatStyle.Flat
		});
		_daqProfileGrid.Columns.Add(new DataGridViewComboBoxColumn
		{
			Name = "Map",
			HeaderText = "通道映射",
			Width = 125,
			FillWeight = 90f,
			FlatStyle = FlatStyle.Flat,
			Items = 
			{
				(object)"DAQ973A60",
				(object)"DAQ973A-60",
				(object)"101-120/201-220/301-320"
			}
		});
		RefreshVisaResources();
		SyncDaqGridFromText();
	}

	private void RefreshVisaResources()
	{
		SortedSet<string> resources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		string text = _pressureAddr.Text.Trim();
		string text2 = _dmmAddr.Text.Trim();
		AddAddr(_pressureAddr.Text);
		AddAddr(_dmmAddr.Text);
		string[] array = _daqProfiles.Text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string input in array)
		{
			Match match = Regex.Match(input, "GPIB\\d+::\\d+::INSTR", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				AddAddr(match.Value);
			}
		}
		try
		{
			ResourceManager resourceManager = (ResourceManager)Activator.CreateInstance(Marshal.GetTypeFromCLSID(new Guid("DB8CBF1C-D6D3-11D4-AA51-00A024EE30BD")));
			string[] array2 = resourceManager.FindRsrc("GPIB?*INSTR");
			Array array3 = array2;
			if (array3 != null)
			{
				foreach (object item in array3)
				{
					AddAddr(Convert.ToString(item));
				}
			}
			else
			{
				AddAddr(Convert.ToString(array2));
			}
		}
		catch
		{
		}
		if (resources.Count == 0)
		{
			AddAddr(text);
			AddAddr(text2);
		}
		RefreshVisaCombo(_pressureAddr, text, resources);
		RefreshVisaCombo(_dmmAddr, text2, resources);
		foreach (DataGridViewComboBoxColumn item2 in from c in _daqProfileGrid.Columns.OfType<DataGridViewComboBoxColumn>()
			where c.Name == "Visa"
			select c)
		{
			List<string> second = (from DataGridViewRow r in _daqProfileGrid.Rows
				where !r.IsNewRow
				select Convert.ToString(r.Cells["Visa"].Value) into x
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList();
			item2.Items.Clear();
			foreach (string item3 in (from x in resources.Concat(second)
				where !string.IsNullOrWhiteSpace(x)
				select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase))
			{
				item2.Items.Add(item3);
			}
		}
		void AddAddr(string? s)
		{
			if (!string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), "^GPIB\\d+::\\d+::INSTR$", RegexOptions.IgnoreCase))
			{
				resources.Add(s.Trim());
			}
		}
	}

	private static void RefreshVisaCombo(ComboBox combo, string oldValue, IEnumerable<string> resources)
	{
		List<string> list = resources.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (!string.IsNullOrWhiteSpace(oldValue) && !list.Contains<string>(oldValue, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(oldValue);
		}
		combo.Items.Clear();
		combo.Items.AddRange(list.Cast<object>().ToArray());
		if (!string.IsNullOrWhiteSpace(oldValue))
		{
			combo.Text = oldValue;
		}
		else if (combo.Items.Count > 0)
		{
			combo.SelectedIndex = 0;
		}
	}

	private void SyncDaqGridFromText()
	{
		if (_daqProfileGrid.Columns.Count == 0)
		{
			return;
		}
		RefreshVisaResources();
		_daqProfileGrid.Rows.Clear();
		bool flag = false;
		string[] array = _daqProfiles.Text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			string text2 = text.Trim();
			if (text2.Length != 0 && !text2.StartsWith("#"))
			{
				Match match = Regex.Match(text2, "^(\\d+)\\s*-\\s*(\\d+)\\s*=\\s*([^;\\s]+)\\s*;?\\s*(.*)$");
				if (match.Success)
				{
					string text3 = (string.IsNullOrWhiteSpace(match.Groups[4].Value) ? "DAQ973A60" : match.Groups[4].Value.Trim());
					_daqProfileGrid.Rows.Add(true, match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value.Trim(), text3);
					flag = true;
				}
			}
		}
		if (!flag)
		{
			_daqProfileGrid.Rows.Add(true, "1", "60", _dmmAddr.Text.Trim(), "DAQ973A60");
			_daqProfileGrid.Rows.Add(true, "61", "120", "GPIB0::23::INSTR", "DAQ973A60");
		}
	}

	private void SyncDaqProfilesTextFromGrid()
	{
		List<string> list = new List<string>();
		foreach (DataGridViewRow item in (IEnumerable)_daqProfileGrid.Rows)
		{
			if (item.IsNewRow || (item.Cells["Enabled"].Value is bool flag && !flag))
			{
				continue;
			}
			string value = Convert.ToString(item.Cells["From"].Value)?.Trim();
			string value2 = Convert.ToString(item.Cells["To"].Value)?.Trim();
			string value3 = Convert.ToString(item.Cells["Visa"].Value)?.Trim();
			string value4 = Convert.ToString(item.Cells["Map"].Value)?.Trim();
			if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(value2) && !string.IsNullOrWhiteSpace(value3))
			{
				if (string.IsNullOrWhiteSpace(value4))
				{
					value4 = "DAQ973A60";
				}
				list.Add($"{value}-{value2}={value3};{value4}");
			}
		}
		_daqProfiles.Text = string.Join(Environment.NewLine, list);
		string text = list.FirstOrDefault();
		if (text != null)
		{
			Match match = Regex.Match(text, "=([^;]+)");
			if (match.Success)
			{
				_dmmAddr.Text = match.Groups[1].Value.Trim();
			}
		}
	}

	private void SetupDeviceGrid()
	{
		_deviceGrid.Columns.Clear();
		_deviceGrid.Columns.Add("Type", "类型");
		_deviceGrid.Columns.Add("Model", "型号");
		_deviceGrid.Columns.Add("Mode", "模式");
		_deviceGrid.Columns.Add("Address", "地址");
		_deviceGrid.Columns.Add("Baud", "波特率");
		SyncDeviceGridFromControls();
	}

	private void SyncDeviceGridFromControls()
	{
		if (_deviceGrid.Columns.Count != 0)
		{
			_deviceGrid.Rows.Clear();
			_deviceGrid.Rows.Add("Pressure", _pressureModel.Text, _useGpib.Checked ? "Hw" : "Sim", _pressureAddr.Text, "");
			_deviceGrid.Rows.Add("Oven", _compOvenModel.Text, IsTcpOvenModel(_compOvenModel.Text) ? "TCP/IP" : "RS232", GetOvenEndpointText(), IsTcpOvenModel(_compOvenModel.Text) ? "" : _ovenBaud.Text);
			_deviceGrid.Rows.Add("Dmm", _dmmModel.Text, _useGpib.Checked ? "Hw" : "Sim", _dmmAddr.Text, "");
			DataGridViewRowCollection rows = _deviceGrid.Rows;
			object[] obj = new object[5] { "Board", "Board", null, null, null };
			SerialBoardClient? board = _board;
			obj[2] = ((board != null && board.IsOpen) ? "Open" : "Hw");
			obj[3] = _com.Text;
			obj[4] = _boardBaud.Text;
			rows.Add(obj);
			_deviceGrid.Rows.Add("DAQ-Profile", "多DAQ", _multiDaq.Checked ? "Hw" : "Off", _daqProfiles.Text.Replace(Environment.NewLine, " | "), "");
		}
	}

	private void SetupCommandGrid()
	{
		_commandGrid.Columns.Clear();
		_commandGrid.Columns.Add("Key", "指令名");
		_commandGrid.Columns.Add("Value", "指令模板");
		_commandGrid.Columns[0].Width = 150;
	}

	private void SetupCompensationGrid()
	{
		_compSensorModel.Items.AddRange(new object[4] { "F40_100psi", "F40_150psi", "F40_1MPa", "A1m" });
		if (_compSensorModel.Items.Count > 0)
		{
			_compSensorModel.SelectedIndex = 0;
		}
		_compGrid.Columns.Clear();
		_compGrid.Columns.Add("Slot", "工位号");
		_compGrid.Columns.Add("Serial", "序列号");
		_compGrid.Columns.Add("Fixture", "夹具");
		_compGrid.Columns.Add("FixtureSlot", "夹具工位号");
		_compGrid.Columns.Add("P20", "20%");
		_compGrid.Columns.Add("P80", "80%");
		_compGrid.Columns.Add("P60", "60%");
		_compGrid.Columns.Add("PressureAcc", "压力精度(‰)");
		_compGrid.Columns.Add("TempAcc", "温度精度(℃)");
		_compGrid.Columns.Add("Status", "状态");
		_compGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
		_compGrid.MultiSelect = true;
		_compGrid.RowTemplate.Height = 26;
		_compGrid.ColumnHeadersHeight = 30;
		_compGrid.Columns["Slot"].Width = 72;
		_compGrid.Columns["Serial"].FillWeight = 150f;
		_compGrid.Columns["Fixture"].Width = 62;
		_compGrid.Columns["FixtureSlot"].Width = 88;
		_compGrid.Columns["Status"].FillWeight = 110f;
		RefreshCompensationSlotGrid(showLog: false);
	}

	private void RefreshCompensationSlotGrid(bool showLog)
	{
		if (_compGrid.Columns.Count == 0)
		{
			return;
		}
		int num = ReadNumericUpDownInt(_compStartSlot);
		int num2 = ReadNumericUpDownInt(_compSlotCount);
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		Dictionary<int, string> dictionary2 = new Dictionary<int, string>();
		Dictionary<int, string> dictionary3 = new Dictionary<int, string>();
		foreach (DataGridViewRow item in (IEnumerable)_compGrid.Rows)
		{
			if (!item.IsNewRow)
			{
				string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
				Match match = Regex.Match(input, "\\d+");
				if (match.Success)
				{
					int key = int.Parse(match.Value, CultureInfo.InvariantCulture);
					dictionary[key] = Convert.ToString(item.Cells["Serial"].Value) ?? "";
					dictionary2[key] = Convert.ToString(item.Cells["Fixture"].Value) ?? "";
					dictionary3[key] = Convert.ToString(item.Cells["FixtureSlot"].Value) ?? "";
				}
			}
		}
		_compGrid.Rows.Clear();
		for (int i = 0; i < num2; i++)
		{
			int num3 = num + i;
			int value = i / 8 + 1;
			int value2 = i % 8 + 1;
			string value3;
			string text = ((dictionary.TryGetValue(num3, out value3) && !string.IsNullOrWhiteSpace(value3)) ? value3 : $"{DateTime.Now:yyMMddHH}-9#{value}-{value2}");
			string value4;
			string text2 = ((dictionary2.TryGetValue(num3, out value4) && !string.IsNullOrWhiteSpace(value4)) ? value4 : value.ToString(CultureInfo.InvariantCulture));
			string value5;
			string text3 = ((dictionary3.TryGetValue(num3, out value5) && !string.IsNullOrWhiteSpace(value5)) ? value5 : value2.ToString(CultureInfo.InvariantCulture));
			_compGrid.Rows.Add($"Slot{num3}", text, text2, text3, "", "", "", "", "", "待机");
		}
		if (_compGrid.Rows.Count > 0)
		{
			try
			{
				_compGrid.FirstDisplayedScrollingRowIndex = 0;
			}
			catch
			{
			}
			_compGrid.ClearSelection();
		}
		_compGrid.Refresh();
		if (showLog)
		{
			LogComp($"已生成补偿工位表：起始Slot{num}，数量{num2}，范围Slot{num}~Slot{num + num2 - 1}");
		}
	}

	private void SetupF40TestGrid()
	{
		_testGrid.Columns.Clear();
		_testGrid.Columns.Add("Slot", "工位号");
		_testGrid.Columns.Add("Serial", "序列号");
		_testGrid.Columns.Add("Fixture", "夹具");
		_testGrid.Columns.Add("FixtureSlot", "夹具位");
		_testGrid.Columns.Add("DmmAddress", "DMM/DAQ地址");
		_testGrid.Columns.Add("Channel", "通道");
		_testGrid.Columns.Add("OffsetV", "Offset V");
		_testGrid.Columns.Add("SpanV", "Span V");
		_testGrid.Columns.Add("PHOPct", "PHO %FS");
		_testGrid.Columns.Add("NonLinearPct", "非线性 %FS");
		_testGrid.Columns.Add("TOPct", "TO %FS");
		_testGrid.Columns.Add("TSPct", "TS %FS");
		_testGrid.Columns.Add("AccuracyPct", "精度Max %FS");
		_testGrid.Columns.Add("Status", "状态");
		_testGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
		_testGrid.MultiSelect = true;
		_testGrid.RowTemplate.Height = 26;
		_testGrid.ColumnHeadersHeight = 30;
		_testGrid.Columns["Slot"].Width = 70;
		_testGrid.Columns["Serial"].Width = 150;
		_testGrid.Columns["Fixture"].Width = 62;
		_testGrid.Columns["FixtureSlot"].Width = 70;
		_testGrid.Columns["DmmAddress"].Width = 145;
		_testGrid.Columns["Channel"].Width = 72;
		_testGrid.Columns["Status"].FillWeight = 130f;
		RefreshF40TestSlotGrid(showLog: false);
	}

	private void RefreshF40TestSlotGrid(bool showLog)
	{
		if (_testGrid.Columns.Count == 0)
		{
			return;
		}
		int startSlot = ReadNumericUpDownInt(_testStartSlot);
		int count = ReadNumericUpDownInt(_testSlotCount);
		Dictionary<int, string> serials = new Dictionary<int, string>();
		Dictionary<int, string> fixtures = new Dictionary<int, string>();
		Dictionary<int, string> fixtureSlots = new Dictionary<int, string>();
		foreach (DataGridViewRow item in (IEnumerable)_testGrid.Rows)
		{
			if (item.IsNewRow)
			{
				continue;
			}
			string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
			Match match = Regex.Match(input, "\\d+");
			if (match.Success)
			{
				int key = int.Parse(match.Value, CultureInfo.InvariantCulture);
				serials[key] = Convert.ToString(item.Cells["Serial"].Value) ?? "";
				fixtures[key] = Convert.ToString(item.Cells["Fixture"].Value) ?? "";
				fixtureSlots[key] = Convert.ToString(item.Cells["FixtureSlot"].Value) ?? "";
			}
		}
		Dictionary<int, F40TestSlotTemplate> templates = LoadF40TestSlotTemplates();
		string batch = DateTime.Now.ToString("yyMMddHH", CultureInfo.InvariantCulture);
		_testGrid.Rows.Clear();
		for (int i = 0; i < count; i++)
		{
			int slot = startSlot + i;
			int fixture = i / 8 + 1;
			int fixtureSlot = i % 8 + 1;
			templates.TryGetValue(slot, out F40TestSlotTemplate? template);
			string serial = serials.TryGetValue(slot, out string? oldSerial) && !string.IsNullOrWhiteSpace(oldSerial)
				? oldSerial
				: (!string.IsNullOrWhiteSpace(template?.Serial) ? template.Serial : $"{batch}_8#1_{slot}");
			string fixtureText = fixtures.TryGetValue(slot, out string? oldFixture) && !string.IsNullOrWhiteSpace(oldFixture)
				? oldFixture
				: (!string.IsNullOrWhiteSpace(template?.Fixture) ? template.Fixture : fixture.ToString(CultureInfo.InvariantCulture));
			string fixtureSlotText = fixtureSlots.TryGetValue(slot, out string? oldFixtureSlot) && !string.IsNullOrWhiteSpace(oldFixtureSlot)
				? oldFixtureSlot
				: (!string.IsNullOrWhiteSpace(template?.FixtureSlot) ? template.FixtureSlot : fixtureSlot.ToString(CultureInfo.InvariantCulture));
			_testGrid.Rows.Add($"Slot{slot}", serial, fixtureText, fixtureSlotText, EvalDmmAddress(slot), EvalChannel(slot), "", "", "", "", "", "", "", "待采集");
			if (string.IsNullOrWhiteSpace(serials.GetValueOrDefault(slot)) && template != null)
			{
				_testGrid.Rows[_testGrid.Rows.Count - 1].Cells["Serial"].ToolTipText = "来自 " + template.Source;
			}
			else if (string.IsNullOrWhiteSpace(serials.GetValueOrDefault(slot)))
			{
				_testGrid.Rows[_testGrid.Rows.Count - 1].Cells["Serial"].ToolTipText = $"夹具{fixture}-{fixtureSlot}";
			}
		}
		if (_testGrid.Rows.Count > 0)
		{
			try
			{
				_testGrid.FirstDisplayedScrollingRowIndex = 0;
			}
			catch
			{
			}
			_testGrid.ClearSelection();
		}
		_testGrid.Refresh();
		if (showLog)
		{
			LogTest($"已生成F40测试工位表：起始Slot{startSlot}，数量{count}，范围Slot{startSlot}~Slot{startSlot + count - 1}");
		}
	}

	private Dictionary<int, F40TestSlotTemplate> LoadF40TestSlotTemplates()
	{
		foreach (string path in ResolveF40TestSlotCsvCandidates())
		{
			if (!File.Exists(path))
			{
				continue;
			}
			try
			{
				Dictionary<int, F40TestSlotTemplate> result = new Dictionary<int, F40TestSlotTemplate>();
				string[] lines = File.ReadAllLines(path, DetectTextEncoding(path));
				if (lines.Length == 0)
				{
					continue;
				}
				string[] header = SplitSimpleCsv(lines[0]);
				int slotIndex = FindCsvHeaderIndex(header, "工位", "Slot");
				int serialIndex = FindCsvHeaderIndex(header, "序列号", "Serial");
				int fixtureIndex = FindCsvHeaderIndex(header, "夹具位", "Fixture");
				int fixtureSlotIndex = FindCsvHeaderIndex(header, "夹具工位号", "FixtureSlot");
				if (slotIndex < 0)
				{
					slotIndex = 0;
				}
				if (serialIndex < 0)
				{
					serialIndex = 1;
				}
				if (fixtureIndex < 0)
				{
					fixtureIndex = 6;
				}
				if (fixtureSlotIndex < 0)
				{
					fixtureSlotIndex = 7;
				}
				for (int i = 1; i < lines.Length; i++)
				{
					if (string.IsNullOrWhiteSpace(lines[i]))
					{
						continue;
					}
					string[] cells = SplitSimpleCsv(lines[i]);
					if (cells.Length <= slotIndex)
					{
						continue;
					}
					Match match = Regex.Match(cells[slotIndex], "\\d+");
					if (!match.Success)
					{
						continue;
					}
					int slot = int.Parse(match.Value, CultureInfo.InvariantCulture);
					if (result.ContainsKey(slot))
					{
						continue;
					}
					result[slot] = new F40TestSlotTemplate(
						serialIndex >= 0 && cells.Length > serialIndex ? cells[serialIndex].Trim() : "",
						fixtureIndex >= 0 && cells.Length > fixtureIndex ? cells[fixtureIndex].Trim() : "",
						fixtureSlotIndex >= 0 && cells.Length > fixtureSlotIndex ? cells[fixtureSlotIndex].Trim() : "",
						Path.GetFileName(path));
				}
				if (result.Count > 0)
				{
					return result;
				}
			}
			catch
			{
			}
		}
		return new Dictionary<int, F40TestSlotTemplate>();
	}

	private IEnumerable<string> ResolveF40TestSlotCsvCandidates()
	{
		string[] paths = new string[]
		{
			Path.Combine(SettingDir, "Slot.csv"),
			Path.Combine(AppContext.BaseDirectory, "setting", "Slot.csv"),
			Path.Combine(Environment.CurrentDirectory, "setting", "Slot.csv"),
			"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\F40测试\\setting\\Slot.csv"
		};
		return paths.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static int FindCsvHeaderIndex(string[] header, params string[] names)
	{
		for (int i = 0; i < header.Length; i++)
		{
			string cell = header[i].Trim();
			if (names.Any((string name) => cell.Equals(name, StringComparison.OrdinalIgnoreCase)))
			{
				return i;
			}
		}
		return -1;
	}

	private static int ReadNumericUpDownInt(NumericUpDown box)
	{
		string text = box.Text?.Trim();
		int val = (int)box.Value;
		if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			val = result;
		}
		val = Math.Max((int)box.Minimum, Math.Min((int)box.Maximum, val));
		if ((int)box.Value != val)
		{
			try
			{
				box.Value = val;
			}
			catch
			{
			}
		}
		return val;
	}

	private void SetupManualRawGrid()
	{
		_manualRawGrid.Columns.Clear();
		_manualRawGrid.Columns.Add("Index", "序号");
		_manualRawGrid.Columns.Add("Board", "板卡");
		_manualRawGrid.Columns.Add("LocalSlot", "物理工位");
		_manualRawGrid.Columns.Add("LogicSlot", "逻辑工位");
		_manualRawGrid.Columns.Add("Pressure", "压力原始码");
		_manualRawGrid.Columns.Add("Temp", "温度原始码");
		_manualRawGrid.Columns.Add("Status", "状态");
		_manualRawGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
		_manualRawGrid.RowTemplate.Height = 25;
		_manualRawGrid.ColumnHeadersHeight = 30;
	}

	private void LoadCommandFile()
	{
		_commands.Clear();
		IniFile iniFile = IniFile.Load(CommandPath);
		foreach (string section in iniFile.Sections)
		{
			_commands[section] = new Dictionary<string, string>(iniFile.Section(section), StringComparer.OrdinalIgnoreCase);
		}
		_commandModel.Items.Clear();
		_commandModel.Items.AddRange(_commands.Keys.OrderBy((string x) => x).Cast<object>().ToArray());
		if (_commands.ContainsKey(_pressureModel.Text))
		{
			_commandModel.Text = _pressureModel.Text;
		}
		else if (_commands.ContainsKey(_dmmModel.Text))
		{
			_commandModel.Text = _dmmModel.Text;
		}
		else if (_commandModel.Items.Count > 0)
		{
			_commandModel.SelectedIndex = 0;
		}
		LoadCommandModelToGrid();
	}

	private bool EnsureOriginalTcpOvenCommands()
	{
		const string model = "SIDAUMC1000";
		Dictionary<string, string> commands = _commands.TryGetValue(model, out Dictionary<string, string>? existing)
			? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, (string Wrong, string Correct)> migration = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
		{
			["Open"] = ("1:400:1", "POWER,ON"),
			["Set"] = ("1:402:{0}", "TEMP,S9999"),
			["Stop"] = ("1:400:0", "POWER,OFF"),
			["Read"] = ("1:100:2", "TEMP?"),
			["Mode"] = ("1:400:1", "MODE?"),
			["Type"] = ("1:400:1", "TYPE?")
		};
		bool changed = false;
		foreach (KeyValuePair<string, (string Wrong, string Correct)> entry in migration)
		{
			if (!commands.TryGetValue(entry.Key, out string? current) || string.Equals(current.Trim(), entry.Value.Wrong, StringComparison.OrdinalIgnoreCase))
			{
				commands[entry.Key] = entry.Value.Correct;
				changed = true;
			}
		}
		if (changed)
		{
			_commands[model] = commands;
			if (string.Equals(_commandModel.Text, model, StringComparison.OrdinalIgnoreCase))
			{
				LoadCommandModelToGrid();
			}
		}
		return changed;
	}

	private void LoadCommandModelToGrid()
	{
		_commandGrid.Rows.Clear();
		if (!_commands.TryGetValue(_commandModel.Text, out Dictionary<string, string> value))
		{
			return;
		}
		foreach (KeyValuePair<string, string> item in value)
		{
			_commandGrid.Rows.Add(item.Key, item.Value);
		}
	}

	private void SaveCommandFile()
	{
		string text = _commandModel.Text.Trim();
		if (!string.IsNullOrWhiteSpace(text))
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DataGridViewRow item in (IEnumerable)_commandGrid.Rows)
			{
				if (!item.IsNewRow)
				{
					string text2 = Convert.ToString(item.Cells[0].Value)?.Trim() ?? "";
					string value = Convert.ToString(item.Cells[1].Value)?.Trim() ?? "";
					if (text2.Length > 0)
					{
						dictionary[text2] = value;
					}
				}
			}
			_commands[text] = dictionary;
		}
		WriteCommandFileFromMemory();
	}

	private void WriteCommandFileFromMemory()
	{
		Directory.CreateDirectory(SettingDir);
		StringBuilder stringBuilder = new StringBuilder();
		foreach (string item in _commands.Keys.OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			stringBuilder.Append('[').Append(item).AppendLine("]");
			foreach (KeyValuePair<string, string> item2 in _commands[item])
			{
				stringBuilder.Append(item2.Key).Append(" = \"").Append(EncodeIniValue(item2.Value))
					.AppendLine("\"");
			}
			stringBuilder.AppendLine();
		}
		File.WriteAllText(CommandPath, stringBuilder.ToString(), Encoding.UTF8);
	}

	private static string EncodeIniValue(string value)
	{
		return value.Replace("\r", "\\0D").Replace("\n", "\\0A").Replace("\"", "\\\"");
	}

	private void SaveCommandGridForModel(string model, DataGridView grid)
	{
		model = model.Trim();
		if (string.IsNullOrWhiteSpace(model))
		{
			throw new InvalidOperationException("仪器型号不能为空");
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (DataGridViewRow item in (IEnumerable)grid.Rows)
		{
			if (!item.IsNewRow)
			{
				string text = Convert.ToString(item.Cells[0].Value)?.Trim() ?? "";
				string value = Convert.ToString(item.Cells[1].Value)?.Trim() ?? "";
				if (text.Length > 0)
				{
					dictionary[text] = value;
				}
			}
		}
		_commands[model] = dictionary;
		WriteCommandFileFromMemory();
	}

	private void SaveCommandEntriesForModel(string model, IEnumerable<KeyValuePair<string, string>> entries)
	{
		model = model.Trim();
		if (string.IsNullOrWhiteSpace(model))
		{
			throw new InvalidOperationException("仪器型号不能为空");
		}
		Dictionary<string, string> dict;
		Dictionary<string, string> dictionary = (TryGetCommandSection(model, out dict) ? new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		foreach (KeyValuePair<string, string> entry in entries)
		{
			string text = entry.Key.Trim();
			if (text.Length > 0)
			{
				dictionary[text] = entry.Value.Trim();
			}
		}
		_commands[model] = dictionary;
		WriteCommandFileFromMemory();
	}

	private string CommandFor(string model, string key, string fallback, string? arg = null)
	{
		if (TryGetCommandSection(model, out Dictionary<string, string> dict) && TryGetCommandValue(dict, key, out string value) && !string.IsNullOrWhiteSpace(value))
		{
			value = value.Trim().Trim('"');
			if (arg != null)
			{
				value = value.Replace("9999", arg).Replace("{0}", arg);
			}
			return DecodeCommandEscapes(value);
		}
		string cmd = ((arg == null) ? fallback : fallback.Replace("9999", arg).Replace("{0}", arg));
		return DecodeCommandEscapes(cmd);
	}

	private bool TryGetCommandSection(string model, out Dictionary<string, string> dict)
	{
		if (_commands.TryGetValue(model, out dict))
		{
			return true;
		}
		string text = NormalizeCommandKey(model);
		foreach (KeyValuePair<string, Dictionary<string, string>> command in _commands)
		{
			if (NormalizeCommandKey(command.Key) == text)
			{
				dict = command.Value;
				return true;
			}
		}
		dict = null;
		return false;
	}

	private static bool TryGetCommandValue(IReadOnlyDictionary<string, string> dict, string key, out string value)
	{
		if (dict.TryGetValue(key, out value))
		{
			return true;
		}
		string text = NormalizeCommandKey(key);
		foreach (KeyValuePair<string, string> item in dict)
		{
			if (NormalizeCommandKey(item.Key) == text)
			{
				value = item.Value;
				return true;
			}
		}
		value = "";
		return false;
	}

	private static string NormalizeCommandKey(string key)
	{
		StringBuilder stringBuilder = new StringBuilder(key.Length);
		foreach (char c in key)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(char.ToUpperInvariant(c));
			}
		}
		return stringBuilder.ToString();
	}

	private static string DecodeCommandEscapes(string cmd)
	{
		if (string.IsNullOrEmpty(cmd))
		{
			return cmd;
		}
		return cmd.Replace("\\0D", "\r", StringComparison.OrdinalIgnoreCase).Replace("\\0A", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\r", "\r", StringComparison.OrdinalIgnoreCase)
			.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
			.Replace("\\t", "\t", StringComparison.OrdinalIgnoreCase);
	}

	private void ImportIniCommand()
	{
		using OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "选择要导入的仪器指令INI",
			Filter = "INI文件 (*.ini)|*.ini|所有文件 (*.*)|*.*",
			FileName = CommandPath
		};
		if (openFileDialog.ShowDialog(this) == DialogResult.OK)
		{
			Directory.CreateDirectory(SettingDir);
			File.Copy(openFileDialog.FileName, CommandPath, overwrite: true);
			LoadCommandFile();
			Log("已导入仪器指令模板：" + openFileDialog.FileName, important: true);
		}
	}

	private void BuildModernLayout()
	{
		SuspendLayout();
		BackColor = IndustrialWorkspace;
		MenuStrip menuStrip = BuildMenu();
		menuStrip.Visible = false;
		StatusStrip statusStrip = new StatusStrip
		{
			SizingGrip = false,
			BackColor = Color.FromArgb(224, 231, 235),
			ForeColor = IndustrialText
		};
		ToolStripStatusLabel[] array = new ToolStripStatusLabel[3] { _statusSerial, _statusCsv, _statusSelected };
		foreach (ToolStripStatusLabel toolStripStatusLabel in array)
		{
			toolStripStatusLabel.ForeColor = IndustrialText;
		}
		ToolStripStatusLabel toolStripStatusLabel2 = new ToolStripStatusLabel(" | ")
		{
			ForeColor = Color.FromArgb(142, 154, 162)
		};
		ToolStripStatusLabel toolStripStatusLabel3 = new ToolStripStatusLabel(" | ")
		{
			ForeColor = Color.FromArgb(142, 154, 162)
		};
		statusStrip.Items.AddRange(new ToolStripItem[5] { _statusSerial, toolStripStatusLabel2, _statusCsv, toolStripStatusLabel3, _statusSelected });
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			BackColor = BackColor,
			Padding = new Padding(0)
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(16, 10, 12, 8),
			BackColor = IndustrialHeader
		};
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "F40 生产标定工作站",
			Width = 178,
			Height = 32,
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
			ForeColor = IndustrialText
		});
		Panel contentHost = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = BackColor,
			Padding = new Padding(0)
		};
		List<(string Text, Control Page)> pages = new List<(string, Control)>
		{
			("软件补偿", PageBody(BuildCompensationRunTab())),
			("F40标定", PageBody(BuildIndustrialRunTab())),
			("F40测试", PageBody(BuildF40TestTab())),
			("设备配置", PageBody(BuildDeviceCenterTab())),
			("手动调试", PageBody(BuildManualCenterTab())),
			("指令模板", PageBody(BuildCommandCenterTab())),
			("实时日志", PageBody(BuildDataTab()))
		};
		List<Button> navButtons = new List<Button>();
		for (int j = 0; j < pages.Count; j++)
		{
			int index = j;
			Button button = new Button
			{
				Text = pages[j].Text,
				Width = 92,
				Height = 32,
				Margin = new Padding(0, 0, 5, 0),
				TextAlign = ContentAlignment.MiddleCenter,
				FlatStyle = FlatStyle.Flat,
				BackColor = IndustrialSurfaceAlt,
				ForeColor = IndustrialText,
				Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular)
			};
			button.FlatAppearance.BorderSize = 1;
			button.FlatAppearance.BorderColor = IndustrialHeaderBorder;
			button.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 234, 237);
			button.Click += delegate
			{
				SelectWorkspacePage(index);
			};
			navButtons.Add(button);
			flowLayoutPanel.Controls.Add(button);
		}
		SelectWorkspacePage(0);
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialSurfaceAlt,
			Padding = new Padding(12, 3, 12, 3)
		};
		Label value = new Label
		{
			Text = "当前配置文件：setting\\Setting.ini / setting\\Command.ini；设备配置、补偿和标定共用同一套运行参数。",
			Dock = DockStyle.Fill,
			ForeColor = IndustrialMuted,
			TextAlign = ContentAlignment.MiddleLeft
		};
		_saveConfig.Text = "保存配置";
		_reloadConfig.Text = "重载配置";
		_saveConfig.Dock = DockStyle.Right;
		_reloadConfig.Dock = DockStyle.Right;
		_saveConfig.Width = 88;
		_reloadConfig.Width = 88;
		panel.Controls.Add(value);
		panel.Controls.Add(_saveConfig);
		panel.Controls.Add(_reloadConfig);
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel.Controls.Add(contentHost, 0, 1);
		tableLayoutPanel.Controls.Add(panel, 0, 2);
		base.Controls.Add(tableLayoutPanel);
		base.Controls.Add(statusStrip);
		base.MainMenuStrip = menuStrip;
		StyleControls(this);
		ResumeLayout();
		static Control PageBody(TabPage page)
		{
			Panel panel2 = new Panel
			{
				Dock = DockStyle.Fill,
				BackColor = page.BackColor,
				Padding = page.Padding
			};
			while (page.Controls.Count > 0)
			{
				Control control = page.Controls[0];
				page.Controls.Remove(control);
				control.Dock = DockStyle.Fill;
				panel2.Controls.Add(control);
			}
			return panel2;
		}
		void SelectWorkspacePage(int num)
		{
			contentHost.SuspendLayout();
			contentHost.Controls.Clear();
			Control item = pages[num].Page;
			item.Dock = DockStyle.Fill;
			contentHost.Controls.Add(item);
			contentHost.ResumeLayout();
			for (int k = 0; k < navButtons.Count; k++)
			{
				navButtons[k].BackColor = ((k == num) ? IndustrialAccent : IndustrialSurfaceAlt);
				navButtons[k].ForeColor = ((k == num) ? Color.White : IndustrialText);
				navButtons[k].FlatAppearance.BorderColor = ((k == num) ? Color.FromArgb(0, 151, 196) : IndustrialHeaderBorder);
			}
		}
	}

	private void BuildIndustrialShell()
	{
		SuspendLayout();
		BackColor = IndustrialWorkspace;
		MenuStrip menuStrip = BuildMenu();
		menuStrip.Visible = false;
		StatusStrip statusStrip = new StatusStrip
		{
			Dock = DockStyle.Fill,
			SizingGrip = false,
			BackColor = IndustrialSurfaceAlt,
			ForeColor = IndustrialText,
			Padding = new Padding(0)
		};
		ToolStripStatusLabel[] statusLabels = new ToolStripStatusLabel[3] { _statusSerial, _statusCsv, _statusSelected };
		foreach (ToolStripStatusLabel statusLabel in statusLabels)
		{
			statusLabel.ForeColor = IndustrialText;
		}
		statusStrip.Items.AddRange(new ToolStripItem[5]
		{
			_statusSerial,
			new ToolStripStatusLabel(" | ") { ForeColor = IndustrialMuted },
			_statusCsv,
			new ToolStripStatusLabel(" | ") { ForeColor = IndustrialMuted },
			_statusSelected
		});
		TableLayoutPanel root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			BackColor = IndustrialWorkspace,
			Padding = Padding.Empty
		};
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66f));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
		TableLayoutPanel header = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4,
			RowCount = 1,
			BackColor = Color.FromArgb(32, 46, 54),
			Padding = new Padding(14, 8, 12, 8)
		};
		header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246f));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210f));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 570f));
		TableLayoutPanel identity = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2,
			BackColor = Color.Transparent,
			Margin = Padding.Empty
		};
		identity.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));
		identity.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));
		identity.Controls.Add(new Label
		{
			Text = "F40 智能标定工作站",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.BottomLeft,
			Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
			ForeColor = Color.White
		}, 0, 0);
		identity.Controls.Add(new Label
		{
			Text = $"补偿 · 标定 · 成品测试  |  v{AppUpdateService.CurrentVersionText}",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.TopLeft,
			Font = new Font("Microsoft YaHei UI", 8f),
			ForeColor = Color.FromArgb(177, 196, 204)
		}, 0, 1);
		Label pageTitle = new Label
		{
			Dock = DockStyle.Fill,
			Text = "软件补偿",
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
			ForeColor = Color.FromArgb(224, 234, 238),
			Padding = new Padding(12, 0, 0, 0)
		};
		FlowLayoutPanel headerStatus = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false,
			BackColor = Color.Transparent,
			Padding = new Padding(0, 6, 0, 0),
			Margin = Padding.Empty
		};
		_headerRunPill = HeaderStatus("系统待机", 94);
		_headerOvenPill = HeaderStatus("烘箱", 92);
		_headerDaqPill = HeaderStatus("DAQ", 92);
		_headerPressurePill = HeaderStatus("压力控制器", 112);
		_headerBoardPill = HeaderStatus("板卡串口", 104);
		headerStatus.Controls.Add(_headerRunPill);
		headerStatus.Controls.Add(_headerOvenPill);
		headerStatus.Controls.Add(_headerDaqPill);
		headerStatus.Controls.Add(_headerPressurePill);
		headerStatus.Controls.Add(_headerBoardPill);
		_checkUpdate.Text = "检测更新";
		_checkUpdate.Width = 96;
		_checkUpdate.Height = 32;
		_checkUpdate.Margin = new Padding(8, 4, 0, 0);
		_checkUpdate.FlatStyle = FlatStyle.Flat;
		_checkUpdate.FlatAppearance.BorderColor = IndustrialAccent;
		_checkUpdate.BackColor = IndustrialAccent;
		_checkUpdate.ForeColor = Color.White;
		_checkUpdate.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
		FlowLayoutPanel updateArea = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			BackColor = Color.Transparent,
			Margin = Padding.Empty,
			Padding = new Padding(8, 2, 0, 0)
		};
		updateArea.Controls.Add(new Label
		{
			Text = $"当前 v{AppUpdateService.CurrentVersionText}",
			AutoSize = false,
			Width = 94,
			Height = 40,
			TextAlign = ContentAlignment.MiddleRight,
			ForeColor = Color.FromArgb(177, 196, 204),
			Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold)
		});
		updateArea.Controls.Add(_checkUpdate);
		header.Controls.Add(identity, 0, 0);
		header.Controls.Add(pageTitle, 1, 0);
		header.Controls.Add(updateArea, 2, 0);
		header.Controls.Add(headerStatus, 3, 0);
		TableLayoutPanel workspace = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			BackColor = IndustrialWorkspace,
			Padding = Padding.Empty
		};
		workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184f));
		workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		Panel navHost = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(44, 58, 66),
			Padding = new Padding(10, 14, 10, 10)
		};
		FlowLayoutPanel nav = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			AutoScroll = true,
			BackColor = Color.Transparent
		};
		Panel contentHost = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialWorkspace,
			Padding = new Padding(10)
		};
		List<(string Text, Control Page)> pages = new List<(string, Control)>
		{
			("软件补偿", PageBody(BuildCompensationRunTab())),
			("F40标定", PageBody(BuildIndustrialRunTab())),
			("F40测试", PageBody(BuildF40TestTab())),
			("设备配置", PageBody(BuildDeviceCenterTab())),
			("手动调试", PageBody(BuildManualCenterTab())),
			("指令模板", PageBody(BuildCommandCenterTab())),
			("实时日志", PageBody(BuildDataTab()))
		};
		List<Button> navButtons = new List<Button>();
		for (int i = 0; i < pages.Count; i++)
		{
			if (i == 0)
			{
				nav.Controls.Add(NavCaption("生产任务"));
			}
			else if (i == 3)
			{
				nav.Controls.Add(NavCaption("工程维护"));
			}
			int index = i;
			Button button = new Button
			{
				Text = pages[i].Text,
				Width = 156,
				Height = 42,
				Margin = new Padding(0, 0, 0, 5),
				Padding = new Padding(14, 0, 0, 0),
				TextAlign = ContentAlignment.MiddleLeft,
				FlatStyle = FlatStyle.Flat,
				BackColor = Color.FromArgb(44, 58, 66),
				ForeColor = Color.FromArgb(207, 220, 226),
				Font = new Font("Microsoft YaHei UI", 9.3f, FontStyle.Bold)
			};
			button.FlatAppearance.BorderSize = 0;
			button.FlatAppearance.MouseOverBackColor = Color.FromArgb(61, 79, 88);
			button.Click += delegate
			{
				SelectWorkspacePage(index);
			};
			navButtons.Add(button);
			nav.Controls.Add(button);
		}
		nav.Controls.Add(new Label
		{
			Text = BuildTag,
			Width = 156,
			Height = 34,
			Margin = new Padding(0, 14, 0, 0),
			TextAlign = ContentAlignment.BottomLeft,
			ForeColor = Color.FromArgb(135, 155, 164),
			Font = new Font("Microsoft YaHei UI", 7.8f)
		});
		navHost.Controls.Add(nav);
		workspace.Controls.Add(navHost, 0, 0);
		workspace.Controls.Add(contentHost, 1, 0);
		TableLayoutPanel footer = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			BackColor = IndustrialSurfaceAlt,
			Padding = new Padding(8, 2, 10, 2)
		};
		footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184f));
		_saveConfig.Text = "保存配置";
		_reloadConfig.Text = "重载配置";
		_saveConfig.Width = 82;
		_reloadConfig.Width = 82;
		_saveConfig.Height = 28;
		_reloadConfig.Height = 28;
		FlowLayoutPanel footerActions = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false,
			Margin = Padding.Empty
		};
		footerActions.Controls.Add(_reloadConfig);
		footerActions.Controls.Add(_saveConfig);
		footer.Controls.Add(statusStrip, 0, 0);
		footer.Controls.Add(footerActions, 1, 0);
		root.Controls.Add(header, 0, 0);
		root.Controls.Add(workspace, 0, 1);
		root.Controls.Add(footer, 0, 2);
		base.Controls.Add(root);
		base.MainMenuStrip = menuStrip;
		StyleControls(this);
		_checkUpdate.BackColor = IndustrialAccent;
		_checkUpdate.ForeColor = Color.White;
		_checkUpdate.FlatAppearance.BorderColor = IndustrialAccent;
		_checkUpdate.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 132, 163);
		_checkUpdate.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 105, 132);
		SelectWorkspacePage(0);
		UpdateDeviceStatusPanel();
		ResumeLayout();

		static Label HeaderStatus(string text, int width)
		{
			return new Label
			{
				Text = text,
				Width = width,
				Height = 34,
				Margin = new Padding(4, 0, 0, 0),
				TextAlign = ContentAlignment.MiddleCenter,
				BackColor = Color.FromArgb(52, 70, 79),
				ForeColor = Color.FromArgb(190, 207, 214),
				Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold),
				BorderStyle = BorderStyle.FixedSingle
			};
		}

		static Label NavCaption(string text)
		{
			return new Label
			{
				Text = text,
				Width = 156,
				Height = 28,
				Margin = new Padding(0, 3, 0, 4),
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = Color.FromArgb(135, 155, 164),
				Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold)
			};
		}

		static Control PageBody(TabPage page)
		{
			Panel body = new Panel
			{
				Dock = DockStyle.Fill,
				BackColor = page.BackColor,
				Padding = page.Padding
			};
			while (page.Controls.Count > 0)
			{
				Control control = page.Controls[0];
				page.Controls.Remove(control);
				control.Dock = DockStyle.Fill;
				body.Controls.Add(control);
			}
			return body;
		}

		void SelectWorkspacePage(int index)
		{
			contentHost.SuspendLayout();
			contentHost.Controls.Clear();
			Control page = pages[index].Page;
			page.Dock = DockStyle.Fill;
			contentHost.Controls.Add(page);
			contentHost.ResumeLayout();
			for (int i = 0; i < navButtons.Count; i++)
			{
				navButtons[i].BackColor = ((i == index) ? IndustrialAccent : Color.FromArgb(44, 58, 66));
				navButtons[i].ForeColor = ((i == index) ? Color.White : Color.FromArgb(207, 220, 226));
			}
			pageTitle.Text = pages[index].Text;
		}
	}

	private TabPage BuildDeviceCenterTab()
	{
		TabPage tabPage = new TabPage("设备配置")
		{
			BackColor = Color.FromArgb(233, 238, 244),
			Padding = new Padding(8)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill,
			Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
		};
		TabPage tabPage2 = BuildCompensationDeviceTab();
		tabPage2.Text = "补偿设备";
		TabPage tabPage3 = BuildDeviceTab();
		tabPage3.Text = "标定/DAQ";
		tabControl.TabPages.Add(tabPage2);
		tabControl.TabPages.Add(tabPage3);
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildManualCenterTab()
	{
		TabPage tabPage = new TabPage("手动调试")
		{
			BackColor = Color.FromArgb(233, 238, 244),
			Padding = new Padding(8)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill,
			Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
		};
		TabPage tabPage2 = BuildCompensationManualTab();
		tabPage2.Text = "补偿调试";
		TabPage tabPage3 = BuildManualDebugTab();
		tabPage3.Text = "标定调试";
		TabPage tabPage4 = BuildBoardCommandTab();
		tabPage4.Text = "板卡协议";
		tabControl.TabPages.Add(tabPage2);
		tabControl.TabPages.Add(tabPage3);
		tabControl.TabPages.Add(tabPage4);
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildCommandCenterTab()
	{
		TabPage tabPage = new TabPage("指令模板")
		{
			BackColor = Color.FromArgb(233, 238, 244),
			Padding = new Padding(8)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill,
			Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
		};
		TabPage tabPage2 = BuildCompensationCommandTab();
		tabPage2.Text = "补偿仪器";
		TabPage tabPage3 = BuildInstrumentCommandTab();
		tabPage3.Text = "标定仪器";
		tabControl.TabPages.Add(tabPage2);
		tabControl.TabPages.Add(tabPage3);
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildCalibrationFunctionTab()
	{
		TabPage tabPage = new TabPage("F40标定")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(8)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		tabControl.TabPages.Add(BuildRunTab());
		tabControl.TabPages.Add(BuildManualDebugTab());
		tabControl.TabPages.Add(BuildDeviceTab());
		tabControl.TabPages.Add(BuildInstrumentCommandTab());
		tabControl.TabPages.Add(BuildBoardCommandTab());
		tabControl.TabPages.Add(BuildDataTab());
		tabControl.TabPages.Add(BuildHelpTab());
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildCompensationFunctionTab()
	{
		TabPage tabPage = new TabPage("软件补偿")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(8)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		tabControl.TabPages.Add(BuildCompensationRunTab());
		tabControl.TabPages.Add(BuildCompensationDeviceTab());
		tabControl.TabPages.Add(BuildCompensationCommandTab());
		tabControl.TabPages.Add(BuildCompensationManualTab());
		tabControl.TabPages.Add(BuildCompensationHelpTab());
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildCompensationRunTab()
	{
		TabPage tabPage = new TabPage("补偿运行")
		{
			BackColor = IndustrialWorkspace,
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2,
			BackColor = tabPage.BackColor
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 148f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			BackColor = tabPage.BackColor
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			BackColor = tabPage.BackColor,
			Padding = new Padding(10, 12, 10, 6)
		};
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		_compSensorModel.Width = 270;
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "传感器型号",
			Width = 86,
			Height = 28,
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		});
		flowLayoutPanel.Controls.Add(_compSensorModel);
		flowLayoutPanel.Controls.Add(_compUseOven);
		flowLayoutPanel.Controls.Add(_compUseDebug);
		flowLayoutPanel.Controls.Add(_compAutoConfig);
		flowLayoutPanel.Controls.Add(_compTest);
		flowLayoutPanel.Controls.Add(_compWriteNumber);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel, 0, 0);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		_compOvenModel.Width = 116;
		_boardSlotMap.Width = 190;
		Add(flowLayoutPanel2, "工位", _compStartSlot, "数量", _compSlotCount, "烘箱", _compOvenModel, "板卡范围", _boardSlotMap, _useBoardChannel47, _compWritePreConfig, _compWriteCoefficients);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel2, 0, 1);
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		_compOutputDir.Width = 360;
		_compBrowseOutput.Width = 72;
		Add(flowLayoutPanel3, "P0/P50/P100", _compP0, _compP50, _compP100, "单位", _compPressureUnit, "T1/T2/T3℃", _compT1, _compT2, _compT3, "目录", _compOutputDir, _compBrowseOutput);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel3, 0, 2);
		tableLayoutPanel2.Controls.Add(tableLayoutPanel3, 0, 0);
		TableLayoutPanel tableLayoutPanel4 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2,
			BackColor = tabPage.BackColor
		};
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184f));
		tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Absolute, 190f));
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialConsole,
			BorderStyle = BorderStyle.FixedSingle,
			Margin = new Padding(0, 8, 0, 0),
			Padding = new Padding(8)
		};
		_logComp.Dock = DockStyle.Fill;
		_logComp.BorderStyle = BorderStyle.None;
		_logComp.BackColor = IndustrialConsole;
		_logComp.ForeColor = IndustrialConsoleText;
		_logComp.Font = new Font("Consolas", 10f);
		panel.Controls.Add(_logComp);
		_compGrid.Dock = DockStyle.Fill;
		_compGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
		_compGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
		_compGrid.RowHeadersVisible = false;
		_compGrid.Margin = new Padding(0, 0, 10, 0);
		_compGrid.ScrollBars = ScrollBars.Both;
		if (_compGrid.Columns.Contains("Slot"))
		{
			_compGrid.Columns["Slot"].Width = 90;
		}
		if (_compGrid.Columns.Contains("Serial"))
		{
			_compGrid.Columns["Serial"].Width = 150;
		}
		if (_compGrid.Columns.Contains("Fixture"))
		{
			_compGrid.Columns["Fixture"].Width = 70;
		}
		if (_compGrid.Columns.Contains("FixtureSlot"))
		{
			_compGrid.Columns["FixtureSlot"].Width = 96;
		}
		if (_compGrid.Columns.Contains("P20"))
		{
			_compGrid.Columns["P20"].Width = 72;
		}
		if (_compGrid.Columns.Contains("P80"))
		{
			_compGrid.Columns["P80"].Width = 72;
		}
		if (_compGrid.Columns.Contains("P60"))
		{
			_compGrid.Columns["P60"].Width = 72;
		}
		if (_compGrid.Columns.Contains("PressureAcc"))
		{
			_compGrid.Columns["PressureAcc"].Width = 96;
		}
		if (_compGrid.Columns.Contains("TempAcc"))
		{
			_compGrid.Columns["TempAcc"].Width = 96;
		}
		if (_compGrid.Columns.Contains("Status"))
		{
			_compGrid.Columns["Status"].Width = 74;
		}
		TableLayoutPanel tableLayoutPanel5 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 12,
			BackColor = tabPage.BackColor,
			Padding = new Padding(6, 0, 0, 0)
		};
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
		tableLayoutPanel5.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		Button button = PlainSideButton("编辑仪器配置");
		Button button2 = PlainSideButton("编辑仪器指令");
		Button button3 = PlainSideButton("编辑传感器型号");
		button.Click += delegate
		{
			ShowLegacyDialogSafely("录入仪器配置", ShowDeviceDaqSettingsDialog, LogComp);
		};
		button2.Click += delegate
		{
			ShowLegacyDialogSafely("录入仪器指令", ShowCommandSettingsDialog, LogComp);
		};
		button3.Click += delegate
		{
			_compSensorModel.Focus();
		};
		Button button4 = SideActionButton("刷新工位", IndustrialSurfaceAlt, IndustrialText);
		Button button5 = SideActionButton("探漏测试", IndustrialSurfaceAlt, IndustrialText);
		Button button6 = SideActionButton("输出测试", IndustrialSurfaceAlt, IndustrialText);
		Button button7 = SideActionButton("精度测试", IndustrialSurfaceAlt, IndustrialText);
		Button button8 = SideActionButton("退出系统", IndustrialSurface, IndustrialDanger);
		_compStart.Text = "开始";
		_compStop.Text = "暂停";
		StyleExistingSideButton(_compStart, IndustrialSuccess, Color.White);
		StyleExistingSideButton(_compStop, IndustrialWarning, Color.White);
		tableLayoutPanel5.Controls.Add(RailCaption("生产运行"), 0, 0);
		tableLayoutPanel5.Controls.Add(_compStart, 0, 1);
		tableLayoutPanel5.Controls.Add(_compStop, 0, 2);
		tableLayoutPanel5.Controls.Add(button4, 0, 3);
		tableLayoutPanel5.Controls.Add(RailCaption("质量检查"), 0, 4);
		tableLayoutPanel5.Controls.Add(button5, 0, 5);
		tableLayoutPanel5.Controls.Add(button6, 0, 6);
		tableLayoutPanel5.Controls.Add(button7, 0, 7);
		tableLayoutPanel5.Controls.Add(RailCaption("配置维护"), 0, 8);
		tableLayoutPanel5.Controls.Add(button, 0, 9);
		tableLayoutPanel5.Controls.Add(button2, 0, 10);
		tableLayoutPanel5.Controls.Add(button3, 0, 11);
		button4.Click += delegate
		{
			RefreshCompensationSlotGrid(showLog: true);
		};
		button5.Click += delegate
		{
			LogComp("探漏流程入口：将按切换单元与压力控制器配置执行。");
		};
		_compStart.Click += async delegate
		{
			await SafeRunAsync(AutoCompensateAsync);
		};
		_compStop.Click += delegate
		{
			_cts?.Cancel();
		};
		button6.Click += async delegate
		{
			await SafeRunAsync(RunCompensationOutputTestAsync);
		};
		button7.Click += delegate
		{
			LogComp("精度测试：后续计算压力精度(‰)、温度精度(℃)、线性和回差。");
		};
		button8.Click += delegate
		{
			Close();
		};
		_compBrowseOutput.Click += delegate
		{
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
			{
				Description = "选择补偿原始CSV输出目录",
				SelectedPath = (Directory.Exists(_compOutputDir.Text) ? _compOutputDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
			};
			if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
			{
				_compOutputDir.Text = folderBrowserDialog.SelectedPath;
			}
		};
		tableLayoutPanel4.Controls.Add(_compGrid, 0, 0);
		tableLayoutPanel4.Controls.Add(tableLayoutPanel5, 1, 0);
		tableLayoutPanel4.Controls.Add(panel, 0, 1);
		tableLayoutPanel4.SetColumnSpan(panel, 2);
		tableLayoutPanel.Controls.Add(tableLayoutPanel2, 0, 0);
		tableLayoutPanel.Controls.Add(tableLayoutPanel4, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;

		static Label RailCaption(string text)
		{
			return new Label
			{
				Text = text,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.BottomLeft,
				ForeColor = IndustrialMuted,
				Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
				Padding = new Padding(4, 0, 0, 3)
			};
		}
	}

	private TabPage BuildF40TestTab()
	{
		TabPage tabPage = new TabPage("F40测试")
		{
			BackColor = IndustrialWorkspace,
			Padding = new Padding(12)
		};
		TableLayoutPanel root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2,
			BackColor = tabPage.BackColor
		};
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142f));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		TableLayoutPanel header = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			BackColor = tabPage.BackColor,
			Padding = new Padding(10, 10, 10, 4)
		};
		header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel row1 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		_testSensorModel.Width = 230;
		Add(row1, "测试型号", _testSensorModel, _testLoadPlan, _testUsePressure, _testUseOven, _testUseDmm);
		header.Controls.Add(row1, 0, 0);
		FlowLayoutPanel row2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		Add(row2, "工位", _testStartSlot, "数量", _testSlotCount, "保温s", _testTempHoldSec, "稳压s", _testPressureHoldSec, "精度容差V", _testVoltageTolerance);
		header.Controls.Add(row2, 0, 1);
		FlowLayoutPanel row3 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			BackColor = tabPage.BackColor
		};
		Add(row3, "数据目录", _testOutputDir, _testBrowseOutput);
		header.Controls.Add(row3, 0, 2);
		TableLayoutPanel body = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2,
			BackColor = tabPage.BackColor
		};
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176f));
		body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		body.RowStyles.Add(new RowStyle(SizeType.Absolute, 190f));
		_testGrid.Dock = DockStyle.Fill;
		_testGrid.Margin = new Padding(0, 8, 10, 0);
		_testGrid.ScrollBars = ScrollBars.Both;
		TableLayoutPanel rail = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 9,
			BackColor = tabPage.BackColor,
			Padding = new Padding(6, 8, 0, 0)
		};
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
		rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		Button editDevice = PlainSideButton("设备配置");
		Button editCommand = PlainSideButton("指令模板");
		Button openDir = PlainSideButton("打开数据目录");
		StyleExistingSideButton(_testStart, IndustrialSuccess, Color.White);
		StyleExistingSideButton(_testStop, IndustrialWarning, Color.White);
		rail.Controls.Add(RailCaption("采集运行"), 0, 0);
		rail.Controls.Add(_testStart, 0, 1);
		rail.Controls.Add(_testStop, 0, 2);
		rail.Controls.Add(_testRefreshSlots, 0, 3);
		rail.Controls.Add(RailCaption("配置维护"), 0, 4);
		rail.Controls.Add(editDevice, 0, 5);
		rail.Controls.Add(editCommand, 0, 6);
		rail.Controls.Add(openDir, 0, 7);
		Panel logPanel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialConsole,
			BorderStyle = BorderStyle.FixedSingle,
			Margin = new Padding(0, 8, 0, 0),
			Padding = new Padding(8)
		};
		_logTest.BorderStyle = BorderStyle.None;
		_logTest.BackColor = IndustrialConsole;
		_logTest.ForeColor = IndustrialConsoleText;
		_logTest.Dock = DockStyle.Fill;
		logPanel.Controls.Add(_logTest);
		body.Controls.Add(_testGrid, 0, 0);
		body.Controls.Add(rail, 1, 0);
		body.Controls.Add(logPanel, 0, 1);
		body.SetColumnSpan(logPanel, 2);
		root.Controls.Add(header, 0, 0);
		root.Controls.Add(body, 0, 1);
		tabPage.Controls.Add(root);
		_testLoadPlan.Click += delegate
		{
			LoadF40TestPlanSafe(writeLog: true);
		};
		_testRefreshSlots.Click += delegate
		{
			RefreshF40TestSlotGrid(showLog: true);
		};
		_testStart.Click += async delegate
		{
			await SafeRunAsync(RunF40TestAcquisitionAsync);
		};
		_testStop.Click += delegate
		{
			StopCurrentRun();
		};
		_testBrowseOutput.Click += delegate
		{
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
			{
				Description = "选择F40测试数据输出目录",
				SelectedPath = Directory.Exists(_testOutputDir.Text) ? _testOutputDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
			};
			if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
			{
				_testOutputDir.Text = folderBrowserDialog.SelectedPath;
			}
		};
		_testStartSlot.ValueChanged += delegate
		{
			RefreshF40TestSlotGrid(showLog: false);
		};
		_testSlotCount.ValueChanged += delegate
		{
			RefreshF40TestSlotGrid(showLog: false);
		};
		_testSensorModel.SelectedIndexChanged += delegate
		{
			LoadF40TestPlanSafe(writeLog: true);
		};
		editDevice.Click += delegate
		{
			ShowLegacyDialogSafely("设备配置", ShowDeviceDaqSettingsDialog, LogTest);
		};
		editCommand.Click += delegate
		{
			ShowLegacyDialogSafely("仪器指令", ShowCommandSettingsDialog, LogTest);
		};
		openDir.Click += delegate
		{
			string dir = _testOutputDir.Text.Trim();
			if (Directory.Exists(dir))
			{
				Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
			}
		};
		return tabPage;

		static Label RailCaption(string text)
		{
			return new Label
			{
				Text = text,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.BottomLeft,
				ForeColor = IndustrialMuted,
				Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
				Padding = new Padding(4, 0, 0, 3)
			};
		}
	}

	private static Control DashboardMetricCard(string title, string value, string subText, Color accent)
	{
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			Margin = new Padding(0, 0, 10, 0),
			BackColor = IndustrialSurface,
			BorderStyle = BorderStyle.FixedSingle,
			Padding = new Padding(16, 12, 16, 10)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			BackColor = panel.BackColor
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = title,
			Dock = DockStyle.Fill,
			ForeColor = IndustrialMuted,
			Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold)
		}, 0, 0);
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = value,
			Dock = DockStyle.Fill,
			ForeColor = IndustrialText,
			Font = new Font("Segoe UI", 18f, FontStyle.Bold),
			TextAlign = ContentAlignment.MiddleLeft
		}, 0, 1);
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = subText,
			Dock = DockStyle.Fill,
			ForeColor = accent,
			Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
			TextAlign = ContentAlignment.MiddleLeft
		}, 0, 2);
		panel.Controls.Add(tableLayoutPanel);
		return panel;
	}

	private static Button ToolButton(string text, string icon)
	{
		Button button = new Button
		{
			Text = icon + " " + text,
			Dock = DockStyle.Fill,
			Margin = new Padding(6, 0, 0, 6),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.FromArgb(234, 237, 240),
			ForeColor = Color.FromArgb(65, 75, 90),
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
		button.FlatAppearance.BorderColor = Color.FromArgb(196, 202, 210);
		button.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 231, 238);
		return button;
	}

	private static Button PlainSideButton(string text)
	{
		Button button = new Button
		{
			Text = text,
			Dock = DockStyle.Fill,
			Margin = new Padding(0, 0, 0, 3),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.FromArgb(246, 247, 249),
			ForeColor = Color.FromArgb(17, 24, 39),
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular)
		};
		button.FlatAppearance.BorderColor = Color.FromArgb(168, 176, 186);
		button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 240, 246);
		return button;
	}

	private static Button SideActionButton(string text, Color backColor, Color foreColor)
	{
		Button button = new Button
		{
			Text = text
		};
		StyleExistingSideButton(button, backColor, foreColor);
		return button;
	}

	private static void StyleExistingSideButton(Button button, Color backColor, Color foreColor)
	{
		button.Dock = DockStyle.Fill;
		button.Margin = new Padding(0, 8, 0, 8);
		button.FlatStyle = FlatStyle.Flat;
		button.BackColor = backColor;
		button.ForeColor = foreColor;
		button.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
		button.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
		button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 240, 246);
	}

	private TabPage BuildCompensationDeviceTab()
	{
		TabPage tabPage = new TabPage("补偿仪器配置")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		GroupBox groupBox = Card("设备配置总览");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2,
			Padding = new Padding(10, 8, 10, 8)
		};
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			Margin = Padding.Empty
		};
		Button button = new Button
		{
			Text = "保存配置",
			Width = 96,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button2 = new Button
		{
			Text = "重载配置",
			Width = 96,
			Height = 30
		};
		Button button3 = new Button
		{
			Text = "刷新串口",
			Width = 96,
			Height = 30
		};
		Button buttonOpenBoard = new Button
		{
			Text = "打开板卡串口",
			Width = 116,
			Height = 30,
			BackColor = Color.FromArgb(248, 113, 113),
			ForeColor = Color.White
		};
		Button button4 = new Button
		{
			Text = "刷新VISA",
			Width = 96,
			Height = 30
		};
		button.Click += delegate
		{
			SaveAppConfig();
		};
		button2.Click += delegate
		{
			LoadAppConfig();
			LoadCommandFile();
			ApplyChannelMap();
			LogComp("补偿设备配置已按 Setting.ini / Command.ini 重载。");
		};
		button3.Click += delegate
		{
			RefreshPorts();
			LogComp("已刷新板卡/烘箱串口列表。");
		};
		buttonOpenBoard.Click += delegate
		{
			ToggleSerial();
		};
		button4.Click += delegate
		{
			RefreshVisaResources();
			LogComp("已刷新压力控制器 / DAQ VISA 资源。");
		};
		flowLayoutPanel.Controls.Add(button);
		flowLayoutPanel.Controls.Add(button2);
		flowLayoutPanel.Controls.Add(button3);
		flowLayoutPanel.Controls.Add(buttonOpenBoard);
		flowLayoutPanel.Controls.Add(button4);
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "这里的编辑项直接绑定当前运行配置，不再是占位页面。",
			AutoSize = true,
			Padding = new Padding(12, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		});
		tableLayoutPanel2.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel2.Controls.Add(BuildDeviceStatusPanel(), 0, 1);
		groupBox.Controls.Add(tableLayoutPanel2);
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		tabControl.TabPages.Add(BuildCompensationBoardDevicePage());
		tabControl.TabPages.Add(BuildCompensationOvenDevicePage());
		tabControl.TabPages.Add(BuildCompensationPressureDevicePage());
		tabControl.TabPages.Add(BuildCompensationDaqDevicePage());
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(tabControl, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildCompensationBoardDevicePage()
	{
		TabPage tabPage = new TabPage("采集板")
		{
			BackColor = Color.White,
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));
		ComboBox c = CreateMirrorComboBox(_com, SerialPort.GetPortNames());
		ComboBox c2 = CreateMirrorComboBox(_boardBaud);
		ComboBox c3 = CreateMirrorComboBox(_boardDataBits);
		ComboBox c4 = CreateMirrorComboBox(_boardParity);
		ComboBox c5 = CreateMirrorComboBox(_boardStopBits);
		NumericUpDown c6 = CreateMirrorNumericUpDown(_addr);
		NumericUpDown c7 = CreateMirrorNumericUpDown(_timeout);
		TextBox textBox = CreateMirrorTextBox(_boardSlotMap);
		textBox.Width = 360;
		CheckBox c8 = CreateMirrorCheckBox(_useBoardChannel47, "启用4/7通道");
		GroupBox groupBox = Card("串口与站号");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel2, 0, "板卡COM", c, "站号", c6);
		AddFormRow2(tableLayoutPanel2, 1, "波特率", c2, "超时ms", c7);
		AddFormRow2(tableLayoutPanel2, 2, "数据位", c3, "校验", c4);
		AddFormRow2(tableLayoutPanel2, 3, "停止位", c5, "运行通道", c8);
		groupBox.Controls.Add(tableLayoutPanel2);
		GroupBox groupBox2 = Card("工位映射");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			Padding = new Padding(14, 12, 14, 12)
		};
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = false,
			Margin = Padding.Empty
		};
		Add(flowLayoutPanel, "板卡范围", textBox);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel3.Controls.Add(new Label
		{
			Text = "格式示例：1=1-80;2=81-160。逻辑工位会按这个映射换算到板卡地址和物理 Slot。",
			AutoSize = true,
			Padding = new Padding(8, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		}, 0, 1);
		tableLayoutPanel3.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			BackColor = Color.White,
			Text = "原版工控流程中，板卡串口、站号、超时和工位映射会直接影响自动补偿、F40 标定、批量写配置、0x11 写系数。这里的编辑项已经与真实运行参数同步。"
		}, 0, 2);
		groupBox2.Controls.Add(tableLayoutPanel3);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildCompensationOvenDevicePage()
	{
		TabPage tabPage = new TabPage("烘箱")
		{
			BackColor = Color.White,
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 3
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 122f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
		CheckBox checkBox = CreateMirrorCheckBox(_compUseOven, "启用烘箱控制");
		ComboBox model = CreateMirrorComboBox(_compOvenModel, "GWSEBWT1670", "SIDAUMC1000");
		ComboBox c = CreateMirrorComboBox(_ovenIp, "169.254.174.136", "169.254.1.10");
		ComboBox c2 = CreateMirrorComboBox(_ovenPort, "508");
		ComboBox unitId = CreateMirrorComboBox(_ovenUnitId, "0", "1", "255");
		ComboBox c3 = CreateMirrorComboBox(_ovenCom, SerialPort.GetPortNames());
		ComboBox c4 = CreateMirrorComboBox(_ovenBaud);
		ComboBox c5 = CreateMirrorComboBox(_ovenDataBits);
		ComboBox c6 = CreateMirrorComboBox(_ovenParity);
		ComboBox c7 = CreateMirrorComboBox(_ovenStopBits);
		Label modeHint = new Label
		{
			AutoSize = true,
			Padding = new Padding(0, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		};
		model.TextChanged += delegate
		{
			RefreshOvenModeHint();
		};
		RefreshOvenModeHint();
		GroupBox groupBox = Card("控制方式");
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(14, 12, 14, 8),
			WrapContents = true
		};
		Add(flowLayoutPanel, checkBox, "烘箱型号", model);
		flowLayoutPanel.Controls.Add(modeHint);
		groupBox.Controls.Add(flowLayoutPanel);
		GroupBox groupBox2 = Card("TCP/IP 通讯");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel2, 0, "IP地址", c, "端口", c2);
		AddFormRow2(tableLayoutPanel2, 1, "Unit ID", unitId, "协议", CreateReadOnlyTextBox(() => "Modbus TCP / 0x10 + 0x03", model));
		tableLayoutPanel2.Controls.Add(new Label
		{
			Text = "SIDAUMC1000 使用网口 TCP/IP 文本协议；地址、端口和指令均沿用原补偿程序配置。",
			Dock = DockStyle.Fill,
			ForeColor = Color.FromArgb(71, 85, 105),
			TextAlign = ContentAlignment.MiddleLeft
		}, 0, 2);
		tableLayoutPanel2.SetColumnSpan(tableLayoutPanel2.Controls[tableLayoutPanel2.Controls.Count - 1], 4);
		groupBox2.Controls.Add(tableLayoutPanel2);
		GroupBox groupBox3 = Card("RS232 通讯");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel3, 0, "COM", c3, "波特率", c4);
		AddFormRow2(tableLayoutPanel3, 1, "数据位", c5, "校验", c6);
		AddFormRow2(tableLayoutPanel3, 2, "停止位", c7, "当前地址", CreateReadOnlyTextBox(() => GetOvenEndpointText(model.Text), model, _ovenIp, _ovenPort, _ovenCom));
		groupBox3.Controls.Add(tableLayoutPanel3);
		GroupBox groupBox4 = Card("补偿温控参数");
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(14, 12, 14, 8),
			WrapContents = true
		};
		Add(flowLayoutPanel2, "T1/T2/T3℃", CreateMirrorNumericUpDown(_compT1), CreateMirrorNumericUpDown(_compT2), CreateMirrorNumericUpDown(_compT3), "温差±", CreateMirrorNumericUpDown(_compTempTol), "保温s", CreateMirrorNumericUpDown(_compTempHoldSec));
		groupBox4.Controls.Add(flowLayoutPanel2);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.SetColumnSpan(groupBox, 2);
		tableLayoutPanel.Controls.Add(groupBox2, 0, 1);
		tableLayoutPanel.Controls.Add(groupBox3, 1, 1);
		tableLayoutPanel.Controls.Add(groupBox4, 0, 2);
		tableLayoutPanel.SetColumnSpan(groupBox4, 2);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
		void RefreshOvenModeHint()
		{
			modeHint.Text = (IsTcpOvenModel(model.Text) ? "当前型号按原版走 TCP/IP：SIDAUMC1000 -> IP + 端口。" : "当前型号按原版走 RS232：GWSEBWT1670 -> COM + 串口参数。");
		}
	}

	private TabPage BuildCompensationPressureDevicePage()
	{
		TabPage tabPage = new TabPage("压力控制器")
		{
			BackColor = Color.White,
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
		CheckBox c = CreateMirrorCheckBox(_useGpib, "启用压力控制器");
		ComboBox c2 = CreateMirrorComboBox(_pressureModel);
		ComboBox c3 = CreateMirrorComboBox(_pressureAddr);
		ComboBox c4 = CreateMirrorComboBox(_pressureGpibPort);
		ComboBox c5 = CreateMirrorComboBox(_pressureGpibAddress);
		NumericUpDown c6 = CreateMirrorNumericUpDown(_stableTolKpa);
		NumericUpDown c7 = CreateMirrorNumericUpDown(_stableSec);
		NumericUpDown c8 = CreateMirrorNumericUpDown(_settleSec);
		ComboBox c9 = CreateMirrorComboBox(_compPressureUnit);
		GroupBox groupBox = Card("控制器地址");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel2, 0, "启用", c, "型号", c2);
		AddFormRow2(tableLayoutPanel2, 1, "VISA", c3, "压力单位", c9);
		AddFormRow2(tableLayoutPanel2, 2, "GPIB端口", c4, "GPIB地址", c5);
		groupBox.Controls.Add(tableLayoutPanel2);
		GroupBox groupBox2 = Card("稳压判定");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel3, 0, "稳压±kPa", c6, "稳压s", c7);
		AddFormRow2(tableLayoutPanel3, 1, "读数延时s", c8, "当前地址", CreateReadOnlyTextBox(() => _pressureAddr.Text, _pressureAddr));
		groupBox2.Controls.Add(tableLayoutPanel3);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildCompensationDaqDevicePage()
	{
		TabPage tabPage = new TabPage("DAQ / 万用表")
		{
			BackColor = Color.White,
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));
		ComboBox c = CreateMirrorComboBox(_dmmModel);
		ComboBox c2 = CreateMirrorComboBox(_dmmAddr);
		ComboBox c3 = CreateMirrorComboBox(_dmmGpibPort);
		ComboBox c4 = CreateMirrorComboBox(_dmmGpibAddress);
		CheckBox c5 = CreateMirrorCheckBox(_useDaqChannel, "使用通道映射");
		CheckBox c6 = CreateMirrorCheckBox(_multiDaq, "多台DAQ973A");
		CheckBox c7 = CreateMirrorCheckBox(_daqSkipChannel47, "DAQ跳过4/7");
		TextBox c8 = CreateMirrorTextBox(_channelExpr);
		TextBox textBox = CreateMirrorTextBox(_daqProfiles, multiline: true, 140);
		textBox.ScrollBars = ScrollBars.Vertical;
		textBox.TextChanged += delegate
		{
			SyncDaqGridFromText();
		};
		GroupBox groupBox = Card("采集基础配置");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel2, 0, "型号", c, "默认VISA", c2);
		AddFormRow2(tableLayoutPanel2, 1, "GPIB端口", c3, "GPIB地址", c4);
		AddFormRow2(tableLayoutPanel2, 2, "默认映射", c8, "采集策略", c5);
		AddFormRow2(tableLayoutPanel2, 3, "多机模式", c6, "跳过4/7", c7);
		groupBox.Controls.Add(tableLayoutPanel2);
		GroupBox groupBox2 = Card("多台 DAQ 映射");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			Padding = new Padding(14, 12, 14, 8)
		};
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
		tableLayoutPanel3.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel3.Controls.Add(new Label
		{
			Text = "格式：1-60=GPIB0::22::INSTR;DAQ973A60",
			AutoSize = true,
			ForeColor = Color.FromArgb(71, 85, 105),
			Padding = new Padding(2, 6, 0, 0)
		}, 0, 0);
		tableLayoutPanel3.Controls.Add(textBox, 0, 1);
		tableLayoutPanel3.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			BackColor = Color.White,
			Text = "这个页面直接同步隐藏的 DAQ 配置文本；保存配置后会写回 Setting.ini。更细的分卡映射仍可在 F40 的【设备/DAQ配置】页继续查看。"
		}, 0, 2);
		groupBox2.Controls.Add(tableLayoutPanel3);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private ComboBox CreateMirrorComboBox(ComboBox source, params string[] extraItems)
	{
		ComboBox mirror = new ComboBox
		{
			Width = source.Width,
			DropDownStyle = source.DropDownStyle,
			DropDownWidth = Math.Max(source.DropDownWidth, source.Width)
		};
		foreach (string item in (from x in source.Items.Cast<object>().Select(Convert.ToString).Concat<string>(extraItems)
				.Append(source.Text)
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			mirror.Items.Add(item);
		}
		mirror.Text = source.Text;
		bool syncing = false;
		source.TextChanged += delegate
		{
			SyncFromSource();
		};
		mirror.TextChanged += delegate
		{
			SyncToSource();
		};
		return mirror;
		void SyncFromSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				if (!string.IsNullOrWhiteSpace(source.Text) && !mirror.Items.Cast<object>().Any((object x) => string.Equals(Convert.ToString(x), source.Text, StringComparison.OrdinalIgnoreCase)))
				{
					mirror.Items.Add(source.Text);
				}
				mirror.Text = source.Text;
			}
			finally
			{
				syncing = false;
			}
		}
		void SyncToSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				SetComboText(source, mirror.Text);
			}
			finally
			{
				syncing = false;
			}
		}
	}

	private NumericUpDown CreateMirrorNumericUpDown(NumericUpDown source)
	{
		NumericUpDown mirror = new NumericUpDown
		{
			Width = source.Width,
			Minimum = source.Minimum,
			Maximum = source.Maximum,
			DecimalPlaces = source.DecimalPlaces,
			Increment = source.Increment,
			ThousandsSeparator = source.ThousandsSeparator,
			Value = source.Value
		};
		bool syncing = false;
		source.ValueChanged += delegate
		{
			SyncFromSource();
		};
		mirror.ValueChanged += delegate
		{
			SyncToSource();
		};
		return mirror;
		void SyncFromSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				mirror.Value = ClampDecimal(source.Value, mirror.Minimum, mirror.Maximum);
			}
			finally
			{
				syncing = false;
			}
		}
		void SyncToSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				source.Value = ClampDecimal(mirror.Value, source.Minimum, source.Maximum);
			}
			finally
			{
				syncing = false;
			}
		}
	}

	private TextBox CreateMirrorTextBox(TextBox source, bool multiline = false, int? height = null)
	{
		TextBox mirror = new TextBox
		{
			Width = source.Width,
			Height = (height ?? source.Height),
			Multiline = (multiline || source.Multiline),
			ScrollBars = source.ScrollBars,
			Text = source.Text
		};
		bool syncing = false;
		source.TextChanged += delegate
		{
			SyncFromSource();
		};
		mirror.TextChanged += delegate
		{
			SyncToSource();
		};
		return mirror;
		void SyncFromSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				mirror.Text = source.Text;
			}
			finally
			{
				syncing = false;
			}
		}
		void SyncToSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				source.Text = mirror.Text;
			}
			finally
			{
				syncing = false;
			}
		}
	}

	private CheckBox CreateMirrorCheckBox(CheckBox source, string? text = null)
	{
		CheckBox mirror = new CheckBox
		{
			Text = (text ?? source.Text),
			Checked = source.Checked,
			AutoSize = true
		};
		bool syncing = false;
		source.CheckedChanged += delegate
		{
			SyncFromSource();
		};
		mirror.CheckedChanged += delegate
		{
			SyncToSource();
		};
		return mirror;
		void SyncFromSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				mirror.Checked = source.Checked;
			}
			finally
			{
				syncing = false;
			}
		}
		void SyncToSource()
		{
			if (syncing)
			{
				return;
			}
			syncing = true;
			try
			{
				source.Checked = mirror.Checked;
			}
			finally
			{
				syncing = false;
			}
		}
	}

	private TextBox CreateReadOnlyTextBox(Func<string> getter, params Control[] watchedControls)
	{
		TextBox box = new TextBox
		{
			ReadOnly = true,
			BackColor = Color.FromArgb(241, 245, 249),
			BorderStyle = BorderStyle.FixedSingle
		};
		foreach (Control control in watchedControls)
		{
			control.TextChanged += delegate
			{
				RefreshBox();
			};
		}
		RefreshBox();
		return box;
		void RefreshBox()
		{
			box.Text = getter();
		}
	}

	private TabPage BuildCompensationCommandTab()
	{
		TabPage tabPage = new TabPage("补偿仪器指令")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		tabControl.TabPages.Add(BuildCommandEditPage("烘箱", "SIDAUMC1000", new string[6] { "Open", "Set", "Stop", "Read", "Mode", "Type" }));
		tabControl.TabPages.Add(BuildCommandEditPage("压力控制器", "WIKA-CPC6050", new string[12]
		{
			"Open", "Machine Type", "UpperLimt", "ZeroCheck", "ReadPressure", "SetMeasure", "SetPressure", "Vent", "SetAbs", "SelfTest",
			"ReadStatus", "SetGaug"
		}));
		tabControl.TabPages.Add(BuildCommandEditPage("切换单元", "Keysight 34970A", new string[2] { "Open", "Close" }));
		tabPage.Controls.Add(tabControl);
		return tabPage;
	}

	private TabPage BuildCommandEditPage(string title, string defaultModel, string[] keys)
	{
		TabPage tabPage = new TabPage(title)
		{
			BackColor = Color.White,
			Padding = new Padding(22)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 55f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill
		};
		ComboBox model = ComboWith(defaultModel, _commands.Keys.Concat(new string[1] { defaultModel }).Distinct().ToArray());
		ComboBox comboBox = ComboWith((title == "烘箱") ? "TCP/IP" : "GPIB", "RS232", "GPIB", "TCP/IP");
		Add(flowLayoutPanel, title + "型号", model, "通信类型", comboBox);
		DataGridView grid = new DataGridView
		{
			Dock = DockStyle.Fill,
			AllowUserToAddRows = true,
			RowHeadersVisible = false,
			AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
		};
		grid.Columns.Add("Key", "指令名");
		grid.Columns.Add("Value", "指令模板");
		FillGrid(model.Text);
		model.TextChanged += delegate
		{
			FillGrid(model.Text);
			if (title == "烘箱")
			{
				comboBox.Text = (IsTcpOvenModel(model.Text) ? "TCP/IP" : "RS232");
			}
		};
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft
		};
		Button button = new Button
		{
			Text = "保存",
			Width = 90,
			Height = 32
		};
		Button button2 = new Button
		{
			Text = "返回",
			Width = 90,
			Height = 32,
			ForeColor = Color.Red
		};
		button.Click += delegate
		{
			SaveCommandGridForModel(model.Text, grid);
			LogComp(title + "指令模板已保存到 " + CommandPath + "。");
		};
		button2.Click += delegate
		{
			LogComp("返回主补偿页。");
		};
		flowLayoutPanel2.Controls.AddRange(new Control[2] { button2, button });
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel.Controls.Add(grid, 0, 1);
		tableLayoutPanel.Controls.Add(flowLayoutPanel2, 0, 2);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
		void FillGrid(string selectedModel)
		{
			grid.Rows.Clear();
			TryGetCommandSection(selectedModel, out Dictionary<string, string> dict);
			string[] array = keys;
			foreach (string text in array)
			{
				string value;
				string text2 = ((dict != null && TryGetCommandValue(dict, text, out value)) ? value.Trim().Trim('"') : "");
				grid.Rows.Add(text, text2);
			}
			if (dict != null)
			{
				foreach (KeyValuePair<string, string> kv in dict)
				{
					if (!keys.Any((string k) => NormalizeCommandKey(k) == NormalizeCommandKey(kv.Key)))
					{
						grid.Rows.Add(kv.Key, kv.Value);
					}
				}
			}
		}
	}

	private TabPage BuildCompensationManualTab()
	{
		TabPage tabPage = new TabPage("补偿手动/调试")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		GroupBox groupBox = Card("采集板手动");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3
		};
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Absolute, 78f));
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(10, 5, 10, 3),
			WrapContents = true
		};
		NumericUpDown slot = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 255m,
			Value = 1m,
			Width = 70
		};
		Button button = new Button
		{
			Text = "读原始02",
			Width = 92,
			Height = 30
		};
		Button button2 = new Button
		{
			Text = "读百分12",
			Width = 92,
			Height = 30
		};
		Button button3 = new Button
		{
			Text = "读电压10",
			Width = 92,
			Height = 30
		};
		Button button4 = new Button
		{
			Text = "读地址76",
			Width = 92,
			Height = 30
		};
		Button button5 = new Button
		{
			Text = "进入OWI63",
			Width = 100,
			Height = 30
		};
		Button button6 = new Button
		{
			Text = "退出OWI61",
			Width = 100,
			Height = 30
		};
		Button button7 = new Button
		{
			Text = "握手AA",
			Width = 92,
			Height = 30
		};
		Add(flowLayoutPanel, "Slot", slot, button7, button, button2, button3, button4, button5, button6);
		button7.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(170, Array.Empty<byte>(), 4, ct);
			});
		};
		button.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(2, new byte[1] { (byte)slot.Value }, 13, ct);
			});
		};
		button2.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(18, new byte[1] { (byte)slot.Value }, 13, ct);
			});
		};
		button3.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(16, new byte[1] { (byte)slot.Value }, 7, ct);
			});
		};
		button4.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(118, new byte[1] { (byte)slot.Value }, 6, ct);
			});
		};
		button5.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(99, new byte[1] { (byte)slot.Value }, 5, ct);
			});
		};
		button6.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(97, new byte[1] { (byte)slot.Value }, 5, ct);
			});
		};
		tableLayoutPanel2.Controls.Add(flowLayoutPanel, 0, 0);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(10, 3, 10, 3),
			WrapContents = true
		};
		NumericUpDown batchStartBoard = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 247m,
			Value = 1m,
			Width = 58
		};
		NumericUpDown batchStartSlot = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 255m,
			Value = 1m,
			Width = 58
		};
		NumericUpDown batchCount = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 1000m,
			Value = 48m,
			Width = 70
		};
		Button button8 = new Button
		{
			Text = "批量读原始02",
			Width = 118,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button9 = new Button
		{
			Text = "清空表格",
			Width = 90,
			Height = 30
		};
		CheckBox batchUse47 = new CheckBox
		{
			Text = "使用4/7通道",
			Checked = _useBoardChannel47.Checked,
			Width = 115
		};
		batchUse47.CheckedChanged += delegate
		{
			if (_useBoardChannel47.Checked != batchUse47.Checked)
			{
				_useBoardChannel47.Checked = batchUse47.Checked;
			}
		};
		_useBoardChannel47.CheckedChanged += delegate
		{
			if (batchUse47.Checked != _useBoardChannel47.Checked)
			{
				batchUse47.Checked = _useBoardChannel47.Checked;
			}
		};
		Add(flowLayoutPanel2, "起始板卡", batchStartBoard, "起始工位", batchStartSlot, "采集总工位", batchCount, batchUse47, button8, button9);
		tableLayoutPanel2.Controls.Add(flowLayoutPanel2, 0, 1);
		tableLayoutPanel2.Controls.Add(_manualRawGrid, 0, 2);
		button8.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await BatchReadRawAsync((int)batchStartBoard.Value, (int)batchStartSlot.Value, (int)batchCount.Value, ct);
			});
		};
		button9.Click += delegate
		{
			_manualRawGrid.Rows.Clear();
		};
		groupBox.Controls.Add(tableLayoutPanel2);
		GroupBox groupBox2 = Card("写配置 / 写系数");
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(10),
			WrapContents = true,
			AutoScroll = true
		};
		ComboBox reg = ComboWith("0304", "0304", "1415");
		Button button10 = new Button
		{
			Text = "读配置",
			Width = 90,
			Height = 30
		};
		Button button11 = new Button
		{
			Text = "写配置",
			Width = 90,
			Height = 30
		};
		TextBox readA = new TextBox
		{
			Width = 75,
			ReadOnly = true,
			BackColor = Color.FromArgb(226, 232, 240)
		};
		TextBox readB = new TextBox
		{
			Width = 75,
			ReadOnly = true,
			BackColor = Color.FromArgb(226, 232, 240)
		};
		TextBox writeA = new TextBox
		{
			Text = "0001",
			Width = 75
		};
		TextBox writeB = new TextBox
		{
			Text = "0267",
			Width = 75
		};
		NumericUpDown startBoardCfg = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 247m,
			Value = 1m,
			Width = 58
		};
		NumericUpDown startSlotCfg = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 255m,
			Value = 1m,
			Width = 58
		};
		NumericUpDown cfgCount = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 255m,
			Value = 8m,
			Width = 58
		};
		CheckBox cfgUse47 = new CheckBox
		{
			Text = "使用4/7通道",
			Checked = _useBoardChannel47.Checked,
			Width = 115
		};
		cfgUse47.CheckedChanged += delegate
		{
			if (_useBoardChannel47.Checked != cfgUse47.Checked)
			{
				_useBoardChannel47.Checked = cfgUse47.Checked;
			}
		};
		_useBoardChannel47.CheckedChanged += delegate
		{
			if (cfgUse47.Checked != _useBoardChannel47.Checked)
			{
				cfgUse47.Checked = _useBoardChannel47.Checked;
			}
		};
		Button button12 = new Button
		{
			Text = "写系数11",
			Width = 90,
			Height = 30
		};
		Button button13 = new Button
		{
			Text = "重置21",
			Width = 85,
			Height = 30
		};
		Button button14 = new Button
		{
			Text = "读系数73",
			Width = 90,
			Height = 30
		};
		Button button15 = new Button
		{
			Text = "读全寄存器74",
			Width = 115,
			Height = 30
		};
		TextBox iic = new TextBox
		{
			Text = "0000000",
			Width = 100
		};
		Button button16 = new Button
		{
			Text = "写地址77",
			Width = 90,
			Height = 30
		};
		Add(flowLayoutPanel3, "起始板卡", startBoardCfg, "起始工位", startSlotCfg, "配置个数", cfgCount, "寄存器组合", reg, cfgUse47);
		Add(flowLayoutPanel3, "读", readA, readB, button10);
		Add(flowLayoutPanel3, "写", writeA, writeB, button11);
		flowLayoutPanel3.Controls.Add(new Label
		{
			Text = "说明：不勾选“使用4/7通道”时，每板跳过物理Slot25~32、49~56，只使用64个逻辑工位；修复后勾选则恢复80工位。",
			AutoSize = true,
			Padding = new Padding(4, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105),
			Width = 720
		});
		flowLayoutPanel3.SetFlowBreak(flowLayoutPanel3.Controls[flowLayoutPanel3.Controls.Count - 1], value: true);
		Add(flowLayoutPanel3, "高级", button12, button13, button14, button15, "IIC地址", iic, button16);
		button10.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				SerialBoardClient? board = _board;
				if (board == null || !board.IsOpen)
				{
					throw new InvalidOperationException("请先打开板卡串口");
				}
				byte fn = (byte)((reg.Text == "1415") ? 84 : 89);
				BoardSlotTarget target = ResolveBoardSlotFromStart((int)startBoardCfg.Value, (int)startSlotCfg.Value, 0);
				byte[] rsp = await _board.RequestAsync(target.BoardAddr, fn, new byte[1] { target.LocalSlot }, 9, ct);
				LogComp($"读配置 逻辑工位{startSlotCfg.Value} -> 板卡{target.BoardAddr} 物理Slot{target.LocalSlot} RX={Hex(rsp)}");
				if (rsp.Length >= 9 && rsp[1] == fn && rsp[2] == target.LocalSlot)
				{
					readA.Text = $"{rsp[3]:X2}{rsp[4]:X2}";
					readB.Text = $"{rsp[5]:X2}{rsp[6]:X2}";
				}
			});
		};
		button11.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				SerialBoardClient? board = _board;
				if (board == null || !board.IsOpen)
				{
					throw new InvalidOperationException("请先打开板卡串口");
				}
				string group = NormalizeConfigGroup(reg.Text);
				byte[] data = ParseConfigRegisterPair(writeA.Text, writeB.Text);
				int startBoard = (int)startBoardCfg.Value;
				int start = (int)startSlotCfg.Value;
				int count = (int)cfgCount.Value;
				int activeCount = EffectiveBoardSlotCount;
				LogComp($"开始批量写配置：{group} {writeA.Text.Trim()} / {writeB.Text.Trim()}，起始板卡{startBoard}，起始逻辑工位{start}，个数{count}，每板有效{activeCount}个工位，{(_useBoardChannel47.Checked ? "使用" : "跳过")}4/7通道");
				int ok = 0;
				int fail = 0;
				for (int i = 0; i < count; i++)
				{
					ct.ThrowIfCancellationRequested();
					BoardSlotTarget target = ResolveBoardSlotFromStart(startBoard, start, i);
					try
					{
						LogComp($"准备写第{i + 1}/{count}个：板卡{target.BoardAddr} 物理Slot{target.LocalSlot} <- {group} {writeA.Text.Trim()} {writeB.Text.Trim()}");
						await _board.WriteConfigAsync(target.BoardAddr, target.LocalSlot, group, data, ct);
						ok++;
						LogComp($"第{i + 1}/{count}个完成 -> 板卡{target.BoardAddr} 物理Slot{target.LocalSlot} 写配置完成：{group} {writeA.Text.Trim()} {writeB.Text.Trim()}");
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex2)
					{
						fail++;
						LogComp($"第{i + 1}/{count}个失败 -> 板卡{target.BoardAddr} 物理Slot{target.LocalSlot}：{ex2.Message}；继续写下一个");
					}
				}
				LogComp($"批量写配置结束：计划{count}个，成功{ok}个，失败{fail}个。");
			});
		};
		button12.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				F40SlotRow row = _rows.FirstOrDefault((F40SlotRow x) => x.Slot == (int)slot.Value) ?? throw new InvalidOperationException("请先在F40标定页加载CSV，或使用自定义指令发送完整0x11 payload");
				if (row.Coefficients.Length != 10)
				{
					row.CalculateCoefficients(preserveTempCoefficients: true);
				}
				row.EnsureCoefficientsValid();
				BoardSlotTarget target = ResolveBoardSlot(row.Slot);
				await _board.WriteCoefficientsAsync(target.BoardAddr, target.LocalSlot, row.Coefficients, ct);
				LogComp($"GlobalSlot{row.Slot} -> 板卡{target.BoardAddr} Slot{target.LocalSlot} 写系数11完成。");
			});
		};
		button13.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(33, new byte[1] { (byte)slot.Value }, 5, ct);
			});
		};
		button14.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(115, new byte[1] { (byte)slot.Value }, 45, ct);
			});
		};
		button15.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(116, new byte[1] { (byte)slot.Value }, 5, ct);
			});
		};
		button16.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(119, new byte[2]
				{
					(byte)slot.Value,
					ParseIicAddress(iic.Text)
				}, 6, ct);
			});
		};
		groupBox2.Controls.Add(flowLayoutPanel3);
		Control value = BuildCompensationInstrumentManualCard();
		GroupBox groupBox3 = Card("手动日志");
		groupBox3.Controls.Add(_logCompManual);
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		TabPage tabPage2 = new TabPage("板卡手动")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage3 = new TabPage("系数/配置")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage4 = new TabPage("仪器调试")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage5 = new TabPage("日志")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		tabPage2.Controls.Add(groupBox);
		tabPage3.Controls.Add(groupBox2);
		tabPage4.Controls.Add(value);
		tabPage5.Controls.Add(groupBox3);
		tabControl.TabPages.Add(tabPage2);
		tabControl.TabPages.Add(tabPage3);
		tabControl.TabPages.Add(tabPage4);
		tabControl.TabPages.Add(tabPage5);
		tableLayoutPanel.Controls.Add(BuildDeviceStatusPanel(), 0, 0);
		tableLayoutPanel.Controls.Add(tabControl, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private Control BuildCompensationInstrumentManualCard()
	{
		GroupBox groupBox = Card("压力控制器 / 烘箱手动调试");
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		TabPage tabPage = new TabPage("压力控制器")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = true,
			AutoScroll = true,
			Padding = new Padding(8)
		};
		ComboBox pAddr = new ComboBox
		{
			Width = 165,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		pAddr.Items.AddRange(new object[3] { _pressureAddr.Text, "GPIB0::8::INSTR", "GPIB2::16::INSTR" });
		pAddr.Text = _pressureAddr.Text;
		ComboBox pModel = new ComboBox
		{
			Width = 150,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		pModel.Items.AddRange(new object[5] { _pressureModel.Text, "DRUCK-PACE6000", "DRUCK-PACE5000", "WIKA-CPC6050", "FLUKE-6270A" });
		pModel.Text = _pressureModel.Text;
		ComboBox pUnit = ComboWith(_compPressureUnit.Text, "psi", "kPa");
		NumericUpDown pValue = new NumericUpDown
		{
			Minimum = -100000m,
			Maximum = 100000m,
			DecimalPlaces = 3,
			Value = _compP100.Value,
			Width = 82
		};
		TextBox pReadValue = new TextBox
		{
			Width = 115,
			ReadOnly = true
		};
		Button button = new Button
		{
			Text = "读型号",
			Width = 76,
			Height = 30
		};
		Button button2 = new Button
		{
			Text = "设压力",
			Width = 76,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button3 = new Button
		{
			Text = "读压力",
			Width = 76,
			Height = 30
		};
		Button button4 = new Button
		{
			Text = "泄压",
			Width = 70,
			Height = 30
		};
		Add(flowLayoutPanel, "VISA", pAddr, "型号", pModel, "压力", pValue, pUnit, "读数", pReadValue, button, button2, button3, button4);
		tabPage.Controls.Add(flowLayoutPanel);
		button.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithCompPressure(delegate(VisaInstrument inst)
				{
					LogComp("压力控制器型号：" + inst.Query(CommandFor(pModel.Text, "MachineType", "*IDN?")));
					return Task.CompletedTask;
				});
			});
		};
		button2.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithCompPressure(delegate(VisaInstrument inst)
				{
					double value = (double)pValue.Value;
					double num = ConvertPressureToKpa(value, pUnit.Text);
					inst.Write(CommandFor(pModel.Text, "SetPressure", "*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON", num.ToString("0.######", CultureInfo.InvariantCulture)));
					LogComp("手动设压：" + FormatPressureValue(value, pUnit.Text));
					return Task.CompletedTask;
				});
			});
		};
		button3.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithCompPressure(delegate(VisaInstrument inst)
				{
					double value = inst.QueryNumber(CommandFor(pModel.Text, "ReadPressure", "*CLS;SENS?"));
					double user = ConvertPressureFromKpa(value, pUnit.Text);
					BeginInvoke(delegate
					{
						pReadValue.Text = user.ToString("0.######", CultureInfo.InvariantCulture);
					});
					LogComp("手动读取压力：" + FormatPressureValue(user, pUnit.Text, 6));
					return Task.CompletedTask;
				});
			});
		};
		button4.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithCompPressure(delegate(VisaInstrument inst)
				{
					inst.Write(CommandFor(pModel.Text, "Vent", "*CLS;:Sour:Vent 1;:OUTPUT OFF"));
					LogComp("手动泄压命令已发送");
					return Task.CompletedTask;
				});
			});
		};
		TabPage tabPage2 = new TabPage("烘箱")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		tabPage2.Controls.Add(BuildOvenManualCard("烘箱手动调试", LogComp));
		tabControl.TabPages.Add(tabPage);
		tabControl.TabPages.Add(tabPage2);
		groupBox.Controls.Add(tabControl);
		return groupBox;
		async Task WithCompPressure(Func<VisaInstrument, Task> action)
		{
			await Task.Run(async delegate
			{
				using VisaInstrument inst = new VisaInstrument("补偿手动-压力", pAddr.Text.Trim(), Log);
				inst.Open();
				await action(inst);
			});
		}
	}

	private GroupBox BuildOvenManualCard(string title, Action<string> logAction)
	{
		GroupBox groupBox = Card(title);
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			WrapContents = true,
			AutoScroll = true,
			Padding = new Padding(10, 8, 10, 8)
		};
		ComboBox model = new ComboBox
		{
			Width = 135,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		model.Items.AddRange(new object[3] { _compOvenModel.Text, "GWSEBWT1670", "SIDAUMC1000" });
		model.Text = _compOvenModel.Text;
		ComboBox endpoint = new ComboBox
		{
			Width = 150,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		endpoint.Items.AddRange((from x in new string[4] { _ovenIp.Text, _ovenCom.Text, "169.254.174.136", "169.254.1.10" }.Concat(SerialPort.GetPortNames())
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
		endpoint.Text = (IsTcpOvenModel(model.Text) ? _ovenIp.Text : _ovenCom.Text);
		ComboBox port = new ComboBox
		{
			Width = 85,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		port.Items.AddRange(new string[2] { _ovenPort.Text, "508" }.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase).Cast<object>()
			.ToArray());
		port.Text = (string.IsNullOrWhiteSpace(_ovenPort.Text) ? "508" : _ovenPort.Text);
		NumericUpDown temp = new NumericUpDown
		{
			Minimum = -80m,
			Maximum = 200m,
			DecimalPlaces = 1,
			Value = _compT2.Value,
			Width = 72
		};
		TextBox readValue = new TextBox
		{
			Width = 105,
			ReadOnly = true
		};
		Button button = new Button
		{
			Text = "运行",
			Width = 70,
			Height = 30
		};
		Button button2 = new Button
		{
			Text = "设置温度",
			Width = 86,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button3 = new Button
		{
			Text = "停止",
			Width = 70,
			Height = 30
		};
		Button button4 = new Button
		{
			Text = "读温度",
			Width = 76,
			Height = 30
		};
		Label endpointLabel = new Label
		{
			AutoSize = true,
			Padding = new Padding(0, 8, 0, 0)
		};
		Label portLabel = new Label
		{
			Text = "端口",
			AutoSize = true,
			Padding = new Padding(0, 8, 0, 0)
		};
		Label hint = new Label
		{
			AutoSize = true,
			Padding = new Padding(6, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		};
		model.TextChanged += delegate
		{
			RefreshMode();
		};
		RefreshMode();
		Add(flowLayoutPanel, endpointLabel, endpoint, "型号", model, portLabel, port, "目标℃", temp, "当前℃", readValue, button2, button, button3, button4);
		flowLayoutPanel.Controls.Add(hint);
		groupBox.Controls.Add(flowLayoutPanel);
		button2.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithOven(delegate(IOvenClient oven)
				{
					string text = temp.Value.ToString("0.#", CultureInfo.InvariantCulture);
					oven.Write(CommandFor(model.Text, "Set", "TEMP,S9999", text));
					logAction("烘箱设温：" + text + "℃");
				});
			});
		};
		button.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithOven(delegate(IOvenClient oven)
				{
					oven.Write(CommandFor(model.Text, "Open", "POWER,ON"));
					logAction("烘箱运行命令已发送");
				});
			});
		};
		button3.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithOven(delegate(IOvenClient oven)
				{
					oven.Write(CommandFor(model.Text, "Stop", "POWER,OFF"));
					logAction("烘箱停止命令已发送");
				});
			});
		};
		button4.Click += async delegate
		{
			await SafeRunAsync(async delegate
			{
				await WithOven(delegate(IOvenClient oven)
				{
					double value = oven.QueryNumber(CommandFor(model.Text, "Read", "TEMP?"));
					BeginInvoke(delegate
					{
						readValue.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
					});
					logAction($"烘箱当前温度：{value:0.##}℃");
				});
			});
		};
		return groupBox;
		void RefreshMode()
		{
			bool flag = IsTcpOvenModel(model.Text);
			endpointLabel.Text = (flag ? "IP" : "COM");
			portLabel.Visible = flag;
			port.Visible = flag;
			if (string.IsNullOrWhiteSpace(endpoint.Text))
			{
				endpoint.Text = (flag ? _ovenIp.Text : _ovenCom.Text);
			}
			hint.Text = (flag ? "SIDAUMC1000 / TCP/IP" : "GWSEBWT1670 / RS232");
		}
		async Task WithOven(Action<IOvenClient> action)
		{
			await Task.Run(delegate
			{
				using IOvenClient ovenClient = CreateOvenClient(model.Text, endpoint.Text, port.Text);
				ovenClient.Open();
				action(ovenClient);
			});
		}
	}

	private TabPage BuildCompensationHelpTab()
	{
		TabPage tabPage = new TabPage("补偿说明")
		{
			BackColor = Color.White,
			Padding = new Padding(18)
		};
		tabPage.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			Font = new Font("Microsoft YaHei UI", 10f),
			Text = "融合版说明\r\n\r\n【软件补偿】负责温压补偿基础系数：\r\n1. 采集多温度、多压力点原始码。\r\n2. 调用 CalibrationL6.dll 计算补偿系数。\r\n3. 按选项写0304配置、写编号或通过0x11写入补偿系数。\r\n4. 可做补偿后验证，但这不是F40测试程序。\r\n\r\n【F40标定】负责模拟输出零点/满点/线性：\r\n1. 读取补偿后的原始 CSV。\r\n2. 读取setting\\CalibrationTestConfig中的原F40标定压力点、输出目标、DAC容差和线性开关。\r\n3. 控制 P0/Pfull，DAQ/DMM 读低点/满点输出。\r\n4. 修正 BridgeDesired 百分比，重新计算并通过0x11写入标定系数。\r\n\r\n【F40测试】负责成品输出测试数据：\r\n1. 读取setting\\TestConfig中的测试温度/压力矩阵和项目开关。\r\n2. 控制烘箱、压力控制器和DMM/DAQ采集输出电压。\r\n3. 按原ASLab口径计算Offset、Span、PHO、非线性、THO/THS、TCO/TCS、TO/TS和精度。\r\n4. 只有原INI开启精度时才判定OK/NG；长漂、老化和板卡码值/写入方案不会误接到DMM矩阵流程。\r\n5. 不写编号、不写配置、不写系数，不调用 CalibrationL6.dll。\r\n\r\n三者共用设备配置、DAQ通道映射和Command.ini指令模板；CalibrationL6.dll只用于补偿/标定计算。"
		});
		return tabPage;
	}

	private static ComboBox ComboWith(string text, params string[] items)
	{
		ComboBox comboBox = new ComboBox
		{
			Width = Math.Max(110, text.Length * 8 + 35),
			DropDownStyle = ComboBoxStyle.DropDown
		};
		comboBox.Items.AddRange(items.Cast<object>().ToArray());
		comboBox.Text = text;
		return comboBox;
	}

	private void AddConfigButtons(FlowLayoutPanel p)
	{
		Button button = new Button
		{
			Text = "保存",
			Width = 90,
			Height = 32
		};
		Button button2 = new Button
		{
			Text = "返回",
			Width = 90,
			Height = 32,
			ForeColor = Color.Red
		};
		button.Click += delegate
		{
			LogComp("补偿仪器配置已在界面修改；后续可写入 setting\\仪器配置.ini / Setting.ini。");
		};
		button2.Click += delegate
		{
			LogComp("返回补偿运行页。");
		};
		p.Controls.Add(button);
		p.Controls.Add(button2);
		p.SetFlowBreak(button2, value: true);
	}

	private Panel BuildDeviceStatusPanel()
	{
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Padding = new Padding(10, 6, 10, 4),
			BackColor = Color.FromArgb(248, 250, 252)
		};
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "设备状态：",
			AutoSize = true,
			Padding = new Padding(0, 6, 6, 0),
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
			ForeColor = Color.FromArgb(30, 41, 59)
		});
		Label label = new Label();
		Label label2 = new Label();
		Label label3 = new Label();
		Label label4 = new Label();
		InitDeviceStatusLabel(label);
		InitDeviceStatusLabel(label2);
		InitDeviceStatusLabel(label3);
		InitDeviceStatusLabel(label4);
		_deviceStatusViews.Add((label, label2, label3, label4));
		flowLayoutPanel.Controls.Add(label);
		flowLayoutPanel.Controls.Add(label2);
		flowLayoutPanel.Controls.Add(label3);
		flowLayoutPanel.Controls.Add(label4);
		UpdateDeviceStatusPanel();
		return flowLayoutPanel;
	}

	private static void InitDeviceStatusLabel(Label label)
	{
		label.AutoSize = false;
		label.Width = 210;
		label.Height = 30;
		label.TextAlign = ContentAlignment.MiddleCenter;
		label.Margin = new Padding(4, 0, 4, 0);
		label.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
		label.BorderStyle = BorderStyle.FixedSingle;
	}

	private void SetDeviceStatus(Label label, string text, bool ok)
	{
		label.Text = text;
		label.BackColor = (ok ? Color.FromArgb(4, 75, 63) : Color.FromArgb(92, 42, 42));
		label.ForeColor = (ok ? Color.FromArgb(83, 255, 191) : Color.FromArgb(255, 138, 138));
	}

	private void UpdateDeviceStatusPanel()
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(UpdateDeviceStatusPanel);
			return;
		}
		SetOpenSerialButtonState();
		for (int num = _deviceStatusViews.Count - 1; num >= 0; num--)
		{
			(Label, Label, Label, Label) tuple = _deviceStatusViews[num];
			if (tuple.Item1.IsDisposed || tuple.Item2.IsDisposed || tuple.Item3.IsDisposed || tuple.Item4.IsDisposed)
			{
				_deviceStatusViews.RemoveAt(num);
			}
			else
			{
				Label item = tuple.Item1;
				SerialBoardClient? board = _board;
				SetDeviceStatus(item, (board != null && board.IsOpen) ? $"板卡串口 已开启 {_com.Text} 站号{_addr.Value}" : "板卡串口 未开启", _board?.IsOpen ?? false);
				SetDeviceStatus(tuple.Item2, (_useGpib.Checked && !string.IsNullOrWhiteSpace(_pressureAddr.Text)) ? ("压力控制器 已配置 " + _pressureAddr.Text) : "压力控制器 未使用/未配置", _useGpib.Checked && !string.IsNullOrWhiteSpace(_pressureAddr.Text));
				SetDeviceStatus(tuple.Item3, (_useGpib.Checked && !string.IsNullOrWhiteSpace(_dmmAddr.Text)) ? ("DAQ/DMM 已配置 " + _dmmAddr.Text) : "DAQ/DMM 未使用/未配置", _useGpib.Checked && !string.IsNullOrWhiteSpace(_dmmAddr.Text));
				SetDeviceStatus(tuple.Item4, (_compUseOven.Checked && HasOvenEndpoint()) ? ("烘箱 已配置 " + GetOvenEndpointText()) : "烘箱 未使用/未配置", _compUseOven.Checked && HasOvenEndpoint());
			}
		}
		if (_headerBoardPill != null)
		{
			SetHeaderPill(_headerBoardPill, "板卡串口", _board?.IsOpen ?? false);
		}
		if (_headerPressurePill != null)
		{
			SetHeaderConfiguredPill(_headerPressurePill, "压力控制器", _useGpib.Checked && !string.IsNullOrWhiteSpace(_pressureAddr.Text));
		}
		if (_headerDaqPill != null)
		{
			SetHeaderConfiguredPill(_headerDaqPill, "DAQ973A", _useGpib.Checked && !string.IsNullOrWhiteSpace(_dmmAddr.Text));
		}
		if (_headerOvenPill != null)
		{
			SetHeaderConfiguredPill(_headerOvenPill, "烘箱", _compUseOven.Checked && HasOvenEndpoint());
		}
		if (_headerRunPill != null)
		{
			bool running = _cts != null;
			_headerRunPill.Text = running ? "任务运行中" : "系统待机";
			_headerRunPill.BackColor = running ? IndustrialSuccess : Color.FromArgb(52, 70, 79);
			_headerRunPill.ForeColor = running ? Color.White : Color.FromArgb(190, 207, 214);
		}
		UpdateCalibrationOverview();
	}

	private static void SetHeaderPill(Label label, string text, bool ok)
	{
		label.Text = text + (ok ? " 已连接" : " 未连接");
		label.BackColor = (ok ? IndustrialSuccess : Color.FromArgb(74, 61, 57));
		label.ForeColor = (ok ? Color.White : Color.FromArgb(235, 199, 187));
	}

	private static void SetHeaderConfiguredPill(Label label, string text, bool configured)
	{
		label.Text = text + (configured ? " 已配置" : " 未启用");
		label.BackColor = configured ? Color.FromArgb(31, 102, 120) : Color.FromArgb(52, 70, 79);
		label.ForeColor = configured ? Color.White : Color.FromArgb(190, 207, 214);
	}

	private void SetOpenSerialButtonState()
	{
		bool flag = _board?.IsOpen ?? false;
		_openSerial.Text = (flag ? "关闭板卡(已开)" : "打开板卡(未开)");
		_openSerial.BackColor = (flag ? Color.FromArgb(34, 197, 94) : Color.FromArgb(248, 113, 113));
		_openSerial.ForeColor = Color.White;
		_openSerial.FlatStyle = FlatStyle.Flat;
		_openSerial.FlatAppearance.BorderSize = 0;
	}

	private void ValidateCompensationInputs()
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先在【设备/DAQ配置】或顶部菜单打开板卡串口。");
		}
		if (string.IsNullOrWhiteSpace(_compSensorModel.Text))
		{
			throw new InvalidOperationException("补偿型号不能为空。");
		}
		if ((int)_compSlotCount.Value <= 0)
		{
			throw new InvalidOperationException("工位数量必须大于0。");
		}
		GetBoardSlotRoutes();
		if (string.IsNullOrWhiteSpace(_compOutputDir.Text))
		{
			throw new InvalidOperationException("补偿CSV输出目录不能为空。");
		}
		if (Math.Abs(_compP100.Value - _compP0.Value) < 0.000001m)
		{
			throw new InvalidOperationException("P100 不能等于 P0。");
		}
		double a = (double)_compP0.Value;
		double value = (double)_compP50.Value;
		double b = (double)_compP100.Value;
		if (!IsBetween(value, a, b))
		{
			throw new InvalidOperationException("P50 必须位于 P0 和 P100 之间。");
		}
		if (_compUseOven.Checked)
		{
			if (!HasOvenEndpoint())
			{
				throw new InvalidOperationException(IsTcpOvenModel(_compOvenModel.Text) ? "已勾选烘箱，但烘箱 IP/端口 为空。" : "已勾选烘箱，但烘箱 COM 为空。");
			}
			if (string.IsNullOrWhiteSpace(_compOvenModel.Text))
			{
				throw new InvalidOperationException("已勾选烘箱，但烘箱型号为空。");
			}
		}
		if (_useGpib.Checked && string.IsNullOrWhiteSpace(_pressureAddr.Text))
		{
			throw new InvalidOperationException("已启用 GPIB，但压力控制器 VISA 地址为空。");
		}
		if (_compWriteCoefficients.Checked)
		{
			SerialBoardClient? board2 = _board;
			if (board2 == null || !board2.IsOpen)
			{
				throw new InvalidOperationException("勾选了算完写0x11，请先打开板卡串口。");
			}
		}
	}

	private static bool IsBetween(double value, double a, double b)
	{
		return value >= Math.Min(a, b) && value <= Math.Max(a, b);
	}

	private async Task AutoCompensateAsync(CancellationToken ct)
	{
		ValidateCompensationInputs();
		List<CompSlotData> slots = ReadCompensationSlots();
		if (slots.Count == 0)
		{
			throw new InvalidOperationException("工位表为空");
		}
		Directory.CreateDirectory(_compOutputDir.Text.Trim());
		LogComp($"开始自动补偿：型号={_compSensorModel.Text} 工位={slots.Count} 输出={_compOutputDir.Text}");
		using VisaInstrument pressure = (_useGpib.Checked ? new VisaInstrument("PRESS-COMP", _pressureAddr.Text.Trim(), Log) : null);
		pressure?.Open();
		using IOvenClient oven = ((_compUseOven.Checked && HasOvenEndpoint()) ? CreateOvenClient() : null);
		oven?.Open();
		if (_compUseDebug.Checked)
		{
			await RunCompPressureZeroAsync(pressure, ct);
		}
		if (_compWritePreConfig.Checked || _compAutoConfig.Checked)
		{
			LogComp("补偿前写配置：0304 = CC05 / 0300");
			byte[] data = ParseConfigRegisterPair("CC05", "0300");
			foreach (int slot in slots.Select((CompSlotData x) => x.Slot))
			{
				ct.ThrowIfCancellationRequested();
				BoardSlotTarget target = ResolveBoardSlot(slot);
				await _board.WriteConfigAsync(target.BoardAddr, target.LocalSlot, "0304", data, ct);
				SetCompStatus(slot, "已写0304");
				LogComp($"GlobalSlot{slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 写0304完成");
			}
			foreach (CompSlotData slot2 in slots)
			{
				slot2.AppliedConfig = "CC050300";
			}
		}
		if (_compAutoConfig.Checked)
		{
			string configCsv = await AutoConfigureCompensationAsync(slots, pressure, ct);
			if (!string.IsNullOrWhiteSpace(configCsv))
			{
				LogComp("配置CSV已生成：" + configCsv);
			}
		}
		else if (_compWritePreConfig.Checked)
		{
			LogComp("开始检验当前基础配置（20% / 80% / 60%）");
			Dictionary<int, CompVerifyResult> verify = await VerifyCompensationBatchAsync(slots, pressure, "基础配置", ct);
			foreach (CompSlotData slot3 in slots)
			{
				if (verify.TryGetValue(slot3.Slot, out CompVerifyResult item))
				{
					ApplyCompVerifyResult(slot3, item, string.IsNullOrWhiteSpace(slot3.AppliedConfig) ? "CC050300" : slot3.AppliedConfig);
				}
				item = null;
			}
			string configCsv2 = SaveCompensationConfigCsv(slots);
			if (!string.IsNullOrWhiteSpace(configCsv2))
			{
				LogComp("配置CSV已生成：" + configCsv2);
			}
		}
		List<CompPoint> points = BuildCompensationPoints();
		foreach (CompSlotData s in slots)
		{
			foreach (CompPoint p in points)
			{
				s.BridgeDesired[p.Index] = p.BridgePercent;
				s.TempDesired[p.Index] = p.TempDeg;
			}
		}
		List<double> tempOrder = new double[3]
		{
			(double)_compT2.Value,
			(double)_compT1.Value,
			(double)_compT3.Value
		}.Distinct().ToList();
		foreach (double tempDeg in tempOrder)
		{
			ct.ThrowIfCancellationRequested();
			List<CompPoint> tempPoints = (from compPoint in points
				where Math.Abs(compPoint.TempDeg - tempDeg) < 1E-06
				orderby compPoint.Index
				select compPoint).ToList();
			if (tempPoints.Count == 0)
			{
				continue;
			}
			await SetAndHoldOvenAsync(oven, tempDeg, ct);
			foreach (CompPoint point in tempPoints)
			{
				ct.ThrowIfCancellationRequested();
				await SetAndHoldPressureAsync(pressure, point.Pressure, ct);
				LogComp($"采集点 {point.Name}：T={point.TempDeg:0.#}℃ P={point.Pressure:0.###}{_compPressureUnit.Text} Desired={point.BridgePercent:0.###}%");
				foreach (CompSlotData slot4 in slots)
				{
					ct.ThrowIfCancellationRequested();
					try
					{
						BoardSlotTarget target2 = ResolveBoardSlot(slot4.Slot);
						(int BridgeRaw, int TempRaw) raw = ParseRaw02Response(await _board.RequestAsync(target2.BoardAddr, 2, new byte[1] { target2.LocalSlot }, 13, ct));
						slot4.BridgeRaw[point.Index] = raw.BridgeRaw;
						slot4.TempRaw[point.Index] = raw.TempRaw;
						SetCompStatus(slot4.Slot, point.Name + " OK");
						LogComp($"GlobalSlot{slot4.Slot} -> 板卡{target2.BoardAddr} LocalSlot{target2.LocalSlot} {point.Name} Raw：Bridge={raw.BridgeRaw} Temp={raw.TempRaw}");
					}
					catch (Exception ex)
					{
						slot4.Ok = false;
						slot4.Error = ex.Message;
						SetCompStatus(slot4.Slot, point.Name + "失败");
						LogComp($"Slot{slot4.Slot} {point.Name} 读取失败：{ex.Message}");
					}
				}
			}
		}
		foreach (CompSlotData slot5 in slots)
		{
			ct.ThrowIfCancellationRequested();
			if (!slot5.Ok || slot5.BridgeRaw.Any((double x) => x <= -99999999.0) || slot5.TempRaw.Any((double x) => x <= -99999999.0))
			{
				slot5.Ok = false;
				SetCompStatus(slot5.Slot, "采集失败");
				continue;
			}
			try
			{
				F40SlotRow row = new F40SlotRow
				{
					Slot = slot5.Slot,
					Serial = slot5.Serial,
					TestResult = 1,
					BridgeRaw = slot5.BridgeRaw.ToArray(),
					BridgeDesiredPercent = slot5.BridgeDesired.ToArray(),
					TempRaw = slot5.TempRaw.ToArray(),
					TempDesiredDeg = slot5.TempDesired.ToArray(),
					OriginalCoefficients = new int[10],
					Coefficients = new int[10]
				};
				row.CalculateCoefficients(preserveTempCoefficients: false);
				slot5.Coefficients = row.Coefficients.ToArray();
				int verifyResult = row.VerifyCoefficients();
				if (verifyResult != 0)
				{
					slot5.Ok = false;
					slot5.Error = $"VerifyCoefficients ret={verifyResult}";
					SetCompStatus(slot5.Slot, "系数结果超差，未写入");
					LogComp($"工位1_{slot5.Slot}系数结果超差，未写入");
					continue;
				}
				SetCompStatus(slot5.Slot, "系数已计算");
				LogComp($"Slot{slot5.Slot} 系数：{string.Join(",", slot5.Coefficients)}");
				if (_compWriteCoefficients.Checked)
				{
					BoardSlotTarget target3 = ResolveBoardSlot(slot5.Slot);
					await _board.WriteCoefficientsAsync(target3.BoardAddr, target3.LocalSlot, slot5.Coefficients, ct);
					SetCompStatus(slot5.Slot, "系数已写入");
					LogComp($"GlobalSlot{slot5.Slot} -> 板卡{target3.BoardAddr} LocalSlot{target3.LocalSlot} 0x11写系数完成");
				}
			}
			catch (Exception ex2)
			{
				Exception ex3 = ex2;
				slot5.Ok = false;
				slot5.Error = ex3.Message;
				SetCompStatus(slot5.Slot, "算系数失败");
				LogComp($"Slot{slot5.Slot} 计算/写系数失败：{ex3.Message}");
			}
		}
		string csv = SaveCompensationCsv(slots, points);
		LogComp("自动补偿完成，原始CSV已生成：" + csv);
		_csvPath.Text = csv;
		LoadCsvSafe(csv);
		if (_compTest.Checked)
		{
			await RunCompensationOutputTestAsync(slots, pressure, oven, ct);
		}
	}

	private async Task RunCompensationOutputTestAsync(CancellationToken ct)
	{
		ValidateCompensationInputs();
		List<CompSlotData> slots = ReadCompensationSlots();
		Directory.CreateDirectory(_compOutputDir.Text.Trim());
		using VisaInstrument pressure = (_useGpib.Checked ? new VisaInstrument("PRESS-COMP-TEST", _pressureAddr.Text.Trim(), Log) : null);
		pressure?.Open();
		using IOvenClient oven = ((_compUseOven.Checked && HasOvenEndpoint()) ? CreateOvenClient() : null);
		oven?.Open();
		await RunCompensationOutputTestAsync(slots, pressure, oven, ct);
	}

	private async Task<string?> RunCompensationOutputTestAsync(List<CompSlotData> slots, VisaInstrument? pressure, IOvenClient? oven, CancellationToken ct)
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口。");
		}
		if (slots.Count == 0)
		{
			throw new InvalidOperationException("工位表为空。");
		}
		CompTestPlan plan = BuildCompensationTestPlan();
		double pressureTol = Math.Max(0.001, Math.Abs((double)_compP100.Value - (double)_compP0.Value) * 2.0 / 1000.0);
		List<CompTestMeasurement> rows = new List<CompTestMeasurement>();
		string? lastCsv = null;
		bool normalShutdownCompleted = false;
		LogComp($"开始测试环节：{plan.Source}，温度点={string.Join(",", plan.Temperatures.Select((double x) => x.ToString("0.###", CultureInfo.InvariantCulture) + "℃"))}，压力点={string.Join(",", plan.Pressures.Select((double x) => x.ToString("0.###", CultureInfo.InvariantCulture) + _compPressureUnit.Text))}");
		try
		{
			foreach (double temp in plan.Temperatures)
			{
				ct.ThrowIfCancellationRequested();
				await SetAndHoldOvenAsync(oven, temp, ct);
				List<CompTestMeasurement> tempRows = new List<CompTestMeasurement>();
				for (int pIndex = 0; pIndex < plan.Pressures.Count; pIndex++)
				{
					double pressurePoint = plan.Pressures[pIndex];
					ct.ThrowIfCancellationRequested();
					LogComp($"开始测试_压力点{pIndex + 1}");
					await SetAndHoldPressureAsync(pressure, pressurePoint, ct);
					foreach (CompSlotData slot in slots)
					{
						ct.ThrowIfCancellationRequested();
						try
						{
							BoardSlotTarget target = ResolveBoardSlot(slot.Slot);
							(double PressurePercent, double TempDeg, bool Valid) cal = ParseCalibrated12Response(await _board.RequestAsync(target.BoardAddr, 18, new byte[1] { target.LocalSlot }, 13, ct));
							double readPressure = ConvertCompCalibratedPercentToPressure(cal.PressurePercent);
							double pErr = Math.Abs(readPressure - pressurePoint);
							double tErr = Math.Abs(cal.TempDeg - temp);
							CompTestMeasurement item = new CompTestMeasurement(slot.Slot, slot.Serial, temp, pressurePoint, readPressure, cal.TempDeg, pErr, tErr, cal.Valid && pErr <= pressureTol, cal.Valid && tErr <= 2.0, cal.Valid);
							rows.Add(item);
							tempRows.Add(item);
							SetCompStatus(slot.Slot, (item.PressurePass && item.TempPass) ? "测试合格" : "测试超差");
							LogComp($"工位1_{slot.Slot}压力 {readPressure:0.######} 温度 {cal.TempDeg:0.######}");
						}
						catch (Exception ex)
						{
							CompTestMeasurement item2 = new CompTestMeasurement(slot.Slot, slot.Serial, temp, pressurePoint, double.NaN, double.NaN, double.NaN, double.NaN, PressurePass: false, TempPass: false, Valid: false);
							rows.Add(item2);
							tempRows.Add(item2);
							SetCompStatus(slot.Slot, "测试失败");
							LogComp($"工位1_{slot.Slot}测试失败：{ex.Message}");
						}
					}
				}
				int pressurePassSlots = (from x in tempRows
					where x.Valid
					group x by x.Slot).Count((IGrouping<int, CompTestMeasurement> g) => g.All((CompTestMeasurement x) => x.PressurePass));
				int tempPassSlots = (from x in tempRows
					where x.Valid
					group x by x.Slot).Count((IGrouping<int, CompTestMeasurement> g) => g.All((CompTestMeasurement x) => x.TempPass));
				int allPassSlots = (from x in tempRows
					where x.Valid
					group x by x.Slot).Count((IGrouping<int, CompTestMeasurement> g) => g.All((CompTestMeasurement x) => x.PressurePass && x.TempPass));
				LogComp($"压力精度（±2‰）合格{pressurePassSlots}个");
				LogComp($"温度精度（±2℃）合格{tempPassSlots}个");
				LogComp($"压力、温度均合格{allPassSlots}个");
				lastCsv = SaveCompensationTestCsv(tempRows, temp);
				LogComp($"温度点 {temp:0.###}℃ 保存测量表格成功：{lastCsv}");
				await VentCompPressureAsync(pressure, ct);
			}
			await ReturnOvenAndStopAfterTestAsync(oven, ct);
			normalShutdownCompleted = true;
			return lastCsv;
		}
		finally
		{
			if (!normalShutdownCompleted)
			{
				await TryEmergencyCompensationShutdownAsync(pressure, oven);
			}
		}
	}

	private void LoadF40TestPlanSafe(bool writeLog)
	{
		try
		{
			_currentF40TestPlan = BuildF40TestPlan(_testSensorModel.Text.Trim());
			decimal planTol = ClampDecimal((decimal)Math.Max(0.0001, _currentF40TestPlan.VoltageToleranceV), _testVoltageTolerance.Minimum, _testVoltageTolerance.Maximum);
			_testVoltageTolerance.Value = planTol;
			_testVoltageTolerance.Enabled = _currentF40TestPlan.AccuracyEnabled;
			if (writeLog)
			{
				LogTest($"已加载F40测试方案：{_currentF40TestPlan.Model}，温度点={string.Join(",", _currentF40TestPlan.Temperatures.Select((double x) => x.ToString("0.###", CultureInfo.InvariantCulture) + "℃"))}，压力点={string.Join(",", _currentF40TestPlan.Pressures.Select((double x) => x.ToString("0.###", CultureInfo.InvariantCulture) + _currentF40TestPlan.PressureUnit))}，精度判定={(_currentF40TestPlan.AccuracyEnabled ? _currentF40TestPlan.AccuracyPercentFs.ToString("0.###", CultureInfo.InvariantCulture) + "%FS" : "关闭")}，来源={_currentF40TestPlan.Source}");
				if (!_currentF40TestPlan.SupportsDmmMatrix)
				{
					LogTest("当前原方案不能由融合版DMM矩阵流程执行：" + _currentF40TestPlan.UnsupportedReason);
				}
			}
		}
		catch (Exception ex)
		{
			_currentF40TestPlan = BuildFallbackF40TestPlan(_testSensorModel.Text.Trim());
			_testVoltageTolerance.Enabled = false;
			LogTest("加载F40测试方案失败，已使用默认9点测试：" + ex.Message);
		}
	}

	private F40TestPlan BuildF40TestPlan(string model)
	{
		model = string.IsNullOrWhiteSpace(model) ? "F40_100psi" : model.Trim();
		foreach (string path in ResolveF40TestConfigCandidates(model))
		{
			IniFile ini = IniFile.Load(path);
			string unit = NormalizeF40TestPressureUnit(FindIniValueContains(ini, "转换单位") ?? FindIniValueContains(ini, "压力单位") ?? DetectCompPressureUnit(model, "psi"));
			List<double> pressures = ReadF40TestPressureValues(ini, "测试点.测试-压力值", unit);
			if (pressures.Count == 0)
			{
				pressures = ReadF40TestPressureValues(ini, "测试压力点", unit);
			}
			List<double> temps = ReadF40TestTemperatureValues(ini, "测试点.测试-温度值");
			if (temps.Count == 0)
			{
				temps = ReadF40TestTemperatureValues(ini, "测试温度点");
			}
			if (pressures.Count == 0 || temps.Count == 0)
			{
				continue;
			}
			double pressureZero = FindIniDoubleContains(ini, 0.0, "压力零点");
			double pressureFull = FindIniDoubleContains(ini, double.NaN, "压力满度");
			if (double.IsNaN(pressureFull))
			{
				pressureFull = FindIniDoubleContains(ini, pressures.Max(), "满量程");
			}
			double outputMin = FindIniDoubleContains(ini, 0.5, "AoutMin");
			double outputMax = FindIniDoubleContains(ini, 4.5, "AoutMax");
			double accuracy = FindIniDoubleContains(ini, 0.25, "传感器精度等级", "精度等级");
			bool zeroOutput = FindIniBoolContains(ini, true, "测试项目参数.零点输出");
			bool fullOutput = FindIniBoolContains(ini, true, "测试项目参数.满量程输出");
			bool pressureHysteresis = FindIniBoolContains(ini, true, "测试项目参数.压力迟滞");
			bool nonLinearity = FindIniBoolContains(ini, true, "测试项目参数.非线性");
			bool temperatureHysteresis = FindIniBoolContains(ini, true, "测试项目参数.温度迟滞");
			bool temperatureDrift = FindIniBoolContains(ini, true, "测试项目参数.温度漂移");
			bool accuracyEnabled = FindIniBoolContains(ini, false, "测试项目参数.精度");
			bool longStability = FindIniBoolContains(ini, false, "行为参数.是否长漂");
			string task = FindIniValueContains(ini, "TestTaskPoint") ?? "";
			string unsupportedReason = GetF40TestUnsupportedReason(model, task, longStability, temps, pressures);
			return new F40TestPlan(
				model,
				temps,
				pressures,
				unit,
				pressureZero,
				pressureFull,
				outputMin,
				outputMax,
				accuracy,
				zeroOutput,
				fullOutput,
				pressureHysteresis,
				nonLinearity,
				temperatureHysteresis,
				temperatureDrift,
				accuracyEnabled,
				task,
				unsupportedReason,
				Path.GetFileName(path));
		}
		return BuildFallbackF40TestPlan(model);
	}

	private F40TestPlan BuildFallbackF40TestPlan(string model)
	{
		string unit = NormalizeF40TestPressureUnit(DetectCompPressureUnit(model, "psi"));
		double full = InferF40TestFullScale(model, unit);
		List<double> pressures = new double[9] { 0.0, full * 0.25, full * 0.5, full * 0.75, full, full * 0.75, full * 0.5, full * 0.25, 0.0 }.ToList();
		List<double> temps = new double[3] { 5.0, 25.0, 45.0 }.ToList();
		return new F40TestPlan(
			string.IsNullOrWhiteSpace(model) ? "F40_100psi" : model,
			temps,
			pressures,
			unit,
			0.0,
			full,
			0.5,
			4.5,
			0.25,
			true,
			true,
			true,
			true,
			true,
			true,
			false,
			"fallback",
			"未找到可解析的原F40测试INI；默认9点仅用于界面预览，不能作为原程序方案运行。",
			"fallback");
	}

	private IEnumerable<string> ResolveF40TestConfigCandidates(string model)
	{
		string file = model.Trim() + ".ini";
		string[] dirs = new string[]
		{
			Path.Combine(SettingDir, "TestConfig"),
			Path.Combine(AppContext.BaseDirectory, "setting", "TestConfig"),
			Path.Combine(Environment.CurrentDirectory, "setting", "TestConfig"),
			"C:\\Users\\Administrator\\Desktop\\逆向\\02_原始软件\\F40测试\\setting\\TestConfig"
		};
		foreach (string dir in dirs.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string path = Path.Combine(dir, file);
			if (File.Exists(path))
			{
				yield return path;
			}
		}
	}

	private List<double> ReadF40TestPressureValues(IniFile ini, string keyPrefix, string defaultUnit)
	{
		List<double> result = new List<double>();
		foreach ((int Index, string Value) item in ReadIndexedIniValues(ini, keyPrefix).OrderBy((x) => x.Index))
		{
			if (TryParseNumberFromText(item.Value, out double value))
			{
				string fromUnit = DetectCompPressureUnit(item.Value, defaultUnit);
				result.Add(ConvertCompPressureUnit(value, fromUnit, defaultUnit));
			}
		}
		return result;
	}

	private static List<double> ReadF40TestTemperatureValues(IniFile ini, string keyPrefix)
	{
		List<double> result = new List<double>();
		foreach ((int Index, string Value) item in ReadIndexedIniValues(ini, keyPrefix).OrderBy((x) => x.Index))
		{
			if (TryParseNumberFromText(item.Value, out double value))
			{
				result.Add(value);
			}
		}
		return result;
	}

	private static string? FindIniValueContains(IniFile ini, params string[] tokens)
	{
		foreach (string section in ini.Sections)
		{
			foreach (KeyValuePair<string, string> item in ini.Section(section))
			{
				if (tokens.All((string token) => item.Key.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
				{
					return item.Value.Trim();
				}
			}
		}
		return null;
	}

	private static double FindIniDoubleContains(IniFile ini, double fallback, params string[] tokens)
	{
		string? value = FindIniValueContains(ini, tokens);
		return value != null && TryParseNumberFromText(value, out double parsed) ? parsed : fallback;
	}

	private static bool FindIniBoolContains(IniFile ini, bool fallback, params string[] tokens)
	{
		string? value = FindIniValueContains(ini, tokens);
		if (value == null)
		{
			return fallback;
		}
		return value.Trim().Trim('"').ToUpperInvariant() switch
		{
			"TRUE" or "1" or "YES" or "Y" => true,
			"FALSE" or "0" or "NO" or "N" => false,
			_ => fallback
		};
	}

	private static string GetF40TestUnsupportedReason(string model, string task, bool longStability, IReadOnlyCollection<double> temperatures, IReadOnlyCollection<double> pressures)
	{
		if (longStability || task.Contains("LongStability", StringComparison.OrdinalIgnoreCase))
		{
			return "原方案是长漂定时采集，不是一次温压DMM矩阵。";
		}
		if (model.Contains("老化", StringComparison.OrdinalIgnoreCase) || task.Contains("DAQ:Aging", StringComparison.OrdinalIgnoreCase))
		{
			return "原方案是老化/温度循环任务，不能按普通温压矩阵执行。";
		}
		if (task.Contains("Read:CalCode", StringComparison.OrdinalIgnoreCase) ||
			task.Contains("Read:RawCode", StringComparison.OrdinalIgnoreCase) ||
			task.Contains("Write:", StringComparison.OrdinalIgnoreCase) ||
			task.Contains("Cal:Coe", StringComparison.OrdinalIgnoreCase))
		{
			return "原方案包含板卡码值采集或产品写入，融合版F40测试页只允许DMM/DAQ无写入采集。";
		}
		if (temperatures.Count == 0 || pressures.Count < 2)
		{
			return "原方案没有可执行的温度/压力测试矩阵。";
		}
		return "";
	}

	private static string NormalizeF40TestPressureUnit(string? unit)
	{
		string text = (unit ?? "").Trim().ToLowerInvariant();
		if (text.Contains("psi"))
		{
			return "psi";
		}
		if (text.Contains("mpa"))
		{
			return "MPa";
		}
		return "kPa";
	}

	private static double InferF40TestFullScale(string model, string unit)
	{
		if (TryParseNumberFromText(model, out double value))
		{
			if (model.IndexOf("MPa", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return string.Equals(unit, "MPa", StringComparison.OrdinalIgnoreCase) ? value : ConvertCompPressureUnit(value, "MPa", unit);
			}
			if (model.IndexOf("kPa", StringComparison.OrdinalIgnoreCase) >= 0 || model.IndexOf("KPa", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return ConvertCompPressureUnit(value, "kPa", unit);
			}
			return ConvertCompPressureUnit(value, "psi", unit);
		}
		return string.Equals(unit, "psi", StringComparison.OrdinalIgnoreCase) ? 100.0 : 689.475729316836;
	}

	private List<F40TestSlotData> ReadF40TestSlots()
	{
		try
		{
			_testGrid.EndEdit();
		}
		catch
		{
		}
		SyncDaqProfilesTextFromGrid();
		List<F40TestSlotData> result = new List<F40TestSlotData>();
		foreach (DataGridViewRow row in (IEnumerable)_testGrid.Rows)
		{
			if (row.IsNewRow)
			{
				continue;
			}
			string input = Convert.ToString(row.Cells["Slot"].Value) ?? "";
			Match match = Regex.Match(input, "\\d+");
			if (!match.Success)
			{
				continue;
			}
			int slot = int.Parse(match.Value, CultureInfo.InvariantCulture);
			string serial = Convert.ToString(row.Cells["Serial"].Value)?.Trim() ?? "";
			string fixture = Convert.ToString(row.Cells["Fixture"].Value)?.Trim() ?? "";
			string fixtureSlot = Convert.ToString(row.Cells["FixtureSlot"].Value)?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(serial))
			{
				serial = $"{DateTime.Now:yyMMddHH}_8#1_{slot}";
			}
			string address = EvalDmmAddress(slot);
			string channel = EvalChannel(slot);
			row.Cells["DmmAddress"].Value = address;
			row.Cells["Channel"].Value = channel;
			result.Add(new F40TestSlotData
			{
				Slot = slot,
				Serial = serial,
				Fixture = fixture,
				FixtureSlot = fixtureSlot,
				DmmAddress = address,
				Channel = channel
			});
		}
		if (result.Count == 0)
		{
			throw new InvalidOperationException("F40测试工位表为空。");
		}
		if (_testUseDmm.Checked && _useDaqChannel.Checked)
		{
			F40TestSlotData? missing = result.FirstOrDefault((F40TestSlotData x) => string.IsNullOrWhiteSpace(x.DmmAddress) || string.IsNullOrWhiteSpace(x.Channel));
			if (missing != null)
			{
				throw new InvalidOperationException($"Slot{missing.Slot} 没有DMM/DAQ地址或通道映射，请检查多DAQ配置。");
			}
		}
		return result.OrderBy((F40TestSlotData x) => x.Slot).ToList();
	}

	private async Task RunF40TestAcquisitionAsync(CancellationToken ct)
	{
		if (_currentF40TestPlan == null || !string.Equals(_currentF40TestPlan.Model, _testSensorModel.Text.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			LoadF40TestPlanSafe(writeLog: true);
		}
		F40TestPlan plan = _currentF40TestPlan ?? BuildFallbackF40TestPlan(_testSensorModel.Text.Trim());
		if (!plan.SupportsDmmMatrix)
		{
			throw new InvalidOperationException($"方案 {plan.Model} 不能在F40测试页运行：{plan.UnsupportedReason}");
		}
		List<F40TestSlotData> slots = ReadF40TestSlots();
		foreach (F40TestSlotData slot in slots)
		{
			slot.Voltages = new double[plan.Temperatures.Count, plan.Pressures.Count];
			for (int i = 0; i < plan.Temperatures.Count; i++)
			{
				for (int j = 0; j < plan.Pressures.Count; j++)
				{
					slot.Voltages[i, j] = double.NaN;
				}
			}
			SetF40TestStatus(slot.Slot, "等待采集");
		}
		Directory.CreateDirectory(_testOutputDir.Text.Trim());
		VisaInstrument? pressure = null;
		IOvenClient? oven = null;
		Dictionary<string, VisaInstrument> dmmPool = new Dictionary<string, VisaInstrument>(StringComparer.OrdinalIgnoreCase);
		bool normalShutdownCompleted = false;
		try
		{
			if (_testUsePressure.Checked)
			{
				pressure = new VisaInstrument("PRESS-F40TEST", _pressureAddr.Text.Trim(), (message, important) => LogTest(message));
				pressure.Open();
			}
			if (_testUseOven.Checked && HasOvenEndpoint())
			{
				oven = CreateOvenClient();
				oven.Open();
			}
			if (_testUseDmm.Checked)
			{
				foreach (string address in slots.Select((F40TestSlotData x) => _useDaqChannel.Checked ? x.DmmAddress : _dmmAddr.Text.Trim()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					VisaInstrument dmm = new VisaInstrument("DMM-F40TEST", address, (message, important) => LogTest(message));
					dmm.Open();
					dmmPool[address] = dmm;
				}
			}
			LogTest($"F40测试采集开始：型号={plan.Model}，工位={slots.Count}，只采集DMM电压，不写产品配置/系数。");
			for (int ti = 0; ti < plan.Temperatures.Count; ti++)
			{
				double temp = plan.Temperatures[ti];
				ct.ThrowIfCancellationRequested();
				await SetAndHoldF40TestOvenAsync(oven, temp, ct);
				for (int pi = 0; pi < plan.Pressures.Count; pi++)
				{
					double pressurePoint = plan.Pressures[pi];
					ct.ThrowIfCancellationRequested();
					LogTest($"设置压力点{pi + 1}  {pressurePoint:0.###}{plan.PressureUnit}");
					await SetAndHoldF40TestPressureAsync(pressure, plan, pressurePoint, ct);
					List<string> voltageTexts = new List<string>();
					foreach (F40TestSlotData slot in slots)
					{
						ct.ThrowIfCancellationRequested();
						try
						{
							double voltage = await MeasureF40TestVoltageAsync(slot, dmmPool, $"T{ti + 1}P{pi + 1}", ct);
							slot.Voltages[ti, pi] = voltage;
							voltageTexts.Add(voltage.ToString("0.######", CultureInfo.InvariantCulture));
							SetF40TestStatus(slot.Slot, $"T{ti + 1}P{pi + 1} {voltage:0.######}V");
						}
						catch (Exception ex)
						{
							slot.Voltages[ti, pi] = double.NaN;
							voltageTexts.Add("");
							SetF40TestStatus(slot.Slot, "采集失败：" + ShortError(ex.Message));
							LogTest($"Slot{slot.Slot} T{ti + 1}P{pi + 1} 采集失败：{ex.Message}");
						}
					}
					LogTest($"T{ti + 1}P{pi + 1} 电压值为:{string.Join(",", voltageTexts)}V");
				}
				await VentF40TestPressureAsync(pressure, ct);
			}
			ApplyF40TestSummary(plan, slots);
			string rawCsv = SaveF40TestRawCsv(plan, slots);
			string summaryCsv = SaveF40TestSummaryCsv(plan, slots);
			LogTest("F40测试采集完成，原始数据：" + rawCsv);
			LogTest("F40测试汇总：" + summaryCsv);
			await ReturnF40TestOvenAndStopAsync(oven, plan, ct);
			normalShutdownCompleted = true;
		}
		finally
		{
			foreach (VisaInstrument dmm in dmmPool.Values)
			{
				dmm.Dispose();
			}
			if (!normalShutdownCompleted)
			{
				await TryEmergencyF40TestShutdownAsync(pressure, oven);
			}
			pressure?.Dispose();
			oven?.Dispose();
		}
	}

	private async Task SetAndHoldF40TestOvenAsync(IOvenClient? oven, double targetDeg, CancellationToken ct)
	{
		TimeSpan hold = TimeSpan.FromSeconds((double)_testTempHoldSec.Value);
		if (oven == null)
		{
			LogTest($"未启用烘箱自动控制：请人工设置 {targetDeg:0.###}℃，等待 {hold.TotalSeconds:0}s");
			await Task.Delay(hold, ct);
			return;
		}
		oven.Write(CommandFor(_compOvenModel.Text, "Open", "POWER,ON"));
		oven.Write(CommandFor(_compOvenModel.Text, "Set", "TEMP,S9999", targetDeg.ToString("0.#", CultureInfo.InvariantCulture)));
		double tol = (double)_compTempTol.Value;
		DateTime? since = null;
		Stopwatch sw = Stopwatch.StartNew();
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			double temp = oven.QueryNumber(CommandFor(_compOvenModel.Text, "Read", "TEMP?"));
			if (Math.Abs(temp - targetDeg) <= tol)
			{
				since ??= DateTime.Now;
			}
			else
			{
				since = null;
			}
			if (hold.TotalSeconds <= 0.0 || (since.HasValue && DateTime.Now - since.Value >= hold))
			{
				LogTest($"烘箱达到 {targetDeg:0.###}℃ 并完成保持");
				return;
			}
			if (sw.Elapsed > TimeSpan.FromHours(3.0))
			{
				throw new TimeoutException("烘箱稳定超时3小时");
			}
			await Task.Delay(2000, ct);
		}
	}

	private async Task SetAndHoldF40TestPressureAsync(VisaInstrument? pressure, F40TestPlan plan, double targetUserUnit, CancellationToken ct)
	{
		TimeSpan hold = TimeSpan.FromSeconds((double)_testPressureHoldSec.Value);
		if (pressure == null)
		{
			LogTest($"未启用压力控制器：请人工设置 {targetUserUnit:0.###}{plan.PressureUnit}，等待 {hold.TotalSeconds:0}s");
			await Task.Delay(hold, ct);
			return;
		}
		double targetKpa = ConvertCompPressureUnit(targetUserUnit, plan.PressureUnit, "kPa");
		pressure.Write(CommandFor(_pressureModel.Text, "SetPressure", "*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON", targetKpa.ToString("0.######", CultureInfo.InvariantCulture)));
		double tol = (double)_stableTolKpa.Value;
		DateTime? since = null;
		Stopwatch sw = Stopwatch.StartNew();
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			double p = pressure.QueryNumber(CommandFor(_pressureModel.Text, "ReadPressure", "*CLS;SENS?"));
			if (Math.Abs(p - targetKpa) <= tol)
			{
				since ??= DateTime.Now;
			}
			else
			{
				since = null;
			}
			if (hold.TotalSeconds <= 0.0 || (since.HasValue && DateTime.Now - since.Value >= hold))
			{
				LogTest($"压力达到 {targetUserUnit:0.###}{plan.PressureUnit} 并完成保持");
				return;
			}
			if (sw.Elapsed > TimeSpan.FromMinutes(10.0))
			{
				throw new TimeoutException("压力稳定超时10分钟");
			}
			await Task.Delay(1000, ct);
		}
	}

	private Task<double> MeasureF40TestVoltageAsync(F40TestSlotData slot, Dictionary<string, VisaInstrument> dmmPool, string tag, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (!_testUseDmm.Checked)
		{
			using InputBox inputBox = new InputBox($"输入 Slot{slot.Slot} {tag}输出电压(V)", "0");
			if (inputBox.ShowDialog(this) != DialogResult.OK)
			{
				throw new OperationCanceledException();
			}
			return Task.FromResult(double.Parse(inputBox.Value, CultureInfo.InvariantCulture));
		}
		string address = _useDaqChannel.Checked ? slot.DmmAddress : _dmmAddr.Text.Trim();
		if (!dmmPool.TryGetValue(address, out VisaInstrument? dmm))
		{
			throw new InvalidOperationException("DMM未打开：" + address);
		}
		double voltage;
		if (_useDaqChannel.Checked)
		{
			if (string.IsNullOrWhiteSpace(slot.Channel))
			{
				throw new InvalidOperationException($"Slot{slot.Slot} 没有DAQ通道映射。");
			}
			dmm.Write(CommandFor(_dmmModel.Text, "Close", "ROUT:CLOS (@9999)", slot.Channel));
			dmm.Write(CommandFor(_dmmModel.Text, "SetVol", "CONF:VOLT (@9999)", slot.Channel));
			voltage = dmm.QueryNumber(CommandFor(_dmmModel.Text, "ReadValue", "READ?"));
			try
			{
				dmm.Write(CommandFor(_dmmModel.Text, "Open", "ROUT:OPEN (@9999)", slot.Channel));
			}
			catch
			{
			}
		}
		else
		{
			dmm.Write(CommandFor(_dmmModel.Text, "SetVol", "CONF:VOLT"));
			voltage = dmm.QueryNumber(CommandFor(_dmmModel.Text, "ReadValue", "READ?"));
		}
		return Task.FromResult(voltage);
	}

	private Task VentF40TestPressureAsync(VisaInstrument? pressure, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (pressure == null)
		{
			LogTest("未启用压力控制器：请人工泄压。");
			return Task.CompletedTask;
		}
		pressure.Write(CommandFor(_pressureModel.Text, "Vent", "*CLS;:Sour:Vent 1;:OUTPUT OFF"));
		LogTest("压力控制器泄压完成");
		return Task.CompletedTask;
	}

	private async Task ReturnF40TestOvenAndStopAsync(IOvenClient? oven, F40TestPlan plan, CancellationToken ct)
	{
		if (oven == null || plan.Temperatures.Count == 0)
		{
			return;
		}
		double roomTemp = plan.Temperatures.OrderBy((double x) => Math.Abs(x - 25.0)).First();
		oven.Write(CommandFor(_compOvenModel.Text, "Set", "TEMP,S9999", roomTemp.ToString("0.#", CultureInfo.InvariantCulture)));
		LogTest($"测试完成，烘箱返回 {roomTemp:0.###}℃ 后停止");
		await Task.Delay(TimeSpan.FromMinutes(4.0), ct);
		oven.Write(CommandFor(_compOvenModel.Text, "Stop", "POWER,OFF"));
	}

	private async Task TryEmergencyF40TestShutdownAsync(VisaInstrument? pressure, IOvenClient? oven)
	{
		try
		{
			await VentF40TestPressureAsync(pressure, CancellationToken.None);
		}
		catch (Exception ex)
		{
			LogTest("异常收尾泄压失败：" + ex.Message);
		}
		try
		{
			oven?.Write(CommandFor(_compOvenModel.Text, "Stop", "POWER,OFF"));
		}
		catch (Exception ex)
		{
			LogTest("异常收尾停止烘箱失败：" + ex.Message);
		}
	}

	private void ApplyF40TestSummary(F40TestPlan plan, List<F40TestSlotData> slots)
	{
		foreach (F40TestSlotData slot in slots)
		{
			F40TestMetricsResult metrics = CalculateF40TestMetrics(plan, slot);
			foreach (DataGridViewRow row in (IEnumerable)_testGrid.Rows)
			{
				if (!row.IsNewRow && Regex.Match(Convert.ToString(row.Cells["Slot"].Value) ?? "", "\\d+").Value == slot.Slot.ToString(CultureInfo.InvariantCulture))
				{
					row.Cells["OffsetV"].Value = plan.ZeroOutputEnabled ? FormatCompCell(metrics.OffsetV) : "";
					row.Cells["SpanV"].Value = plan.FullOutputEnabled ? FormatCompCell(metrics.SpanV) : "";
					row.Cells["PHOPct"].Value = plan.PressureHysteresisEnabled ? FormatCompCell(metrics.PressureHysteresisPercentFs) : "";
					row.Cells["NonLinearPct"].Value = plan.NonLinearityEnabled ? FormatCompCell(metrics.MaxNonLinearityPercentFs) : "";
					row.Cells["TOPct"].Value = plan.TemperatureDriftEnabled ? FormatCompCell(metrics.TotalOffsetPercentFs) : "";
					row.Cells["TSPct"].Value = plan.TemperatureDriftEnabled ? FormatCompCell(metrics.TotalSpanPercentFs) : "";
					row.Cells["AccuracyPct"].Value = plan.AccuracyEnabled ? FormatCompCell(metrics.MaxAccuracyPercentFs) : "";
					row.Cells["Status"].Value = GetF40TestResultText(plan, metrics);
					break;
				}
			}
		}
		_testGrid.Refresh();
	}

	private static F40TestMetricsResult CalculateF40TestMetrics(F40TestPlan plan, F40TestSlotData slot)
	{
		return F40TestMetricsCalculator.Calculate(
			plan.Temperatures,
			plan.Pressures,
			slot.Voltages,
			plan.PressureZero,
			plan.PressureFull,
			plan.OutputMinV,
			plan.OutputMaxV);
	}

	private string GetF40TestResultText(F40TestPlan plan, F40TestMetricsResult metrics)
	{
		if (!metrics.IsComplete)
		{
			return $"采集不完整 {metrics.SampleCount}/{metrics.ExpectedSampleCount}";
		}
		if (!plan.AccuracyEnabled)
		{
			return "统计完成";
		}
		return Math.Abs(metrics.MaxAccuracyErrorV) <= (double)_testVoltageTolerance.Value ? "OK" : "NG";
	}

	private string SaveF40TestRawCsv(F40TestPlan plan, List<F40TestSlotData> slots)
	{
		string dir = BuildF40TestOutputDirectory(plan);
		string path = Path.Combine(dir, $"{SanitizeFileName(plan.Model)}_测试数据_{DateTime.Now:yyMMddHHmmss}.csv");
		StringBuilder sb = new StringBuilder();
		sb.Append("slot");
		for (int ti = 0; ti < plan.Temperatures.Count; ti++)
		{
			for (int pi = 0; pi < plan.Pressures.Count; pi++)
			{
				sb.Append($",T{ti + 1}P{pi + 1}_V");
			}
		}
		sb.AppendLine();
		foreach (F40TestSlotData slot in slots.OrderBy((F40TestSlotData x) => x.Slot))
		{
			sb.Append(Csv(slot.Serial));
			for (int ti = 0; ti < plan.Temperatures.Count; ti++)
			{
				for (int pi = 0; pi < plan.Pressures.Count; pi++)
				{
					sb.Append(',').Append(FormatCompCell(slot.Voltages[ti, pi]));
				}
			}
			sb.AppendLine();
		}
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		File.WriteAllText(path, sb.ToString(), Encoding.GetEncoding("GB18030"));
		return path;
	}

	private string SaveF40TestSummaryCsv(F40TestPlan plan, List<F40TestSlotData> slots)
	{
		string dir = BuildF40TestOutputDirectory(plan);
		string path = Path.Combine(dir, $"{SanitizeFileName(plan.Model)}_测试汇总_{DateTime.Now:yyMMddHHmmss}.csv");
		StringBuilder sb = new StringBuilder();
		List<(F40TestSlotData Slot, F40TestMetricsResult Metrics)> results = slots
			.OrderBy((F40TestSlotData x) => x.Slot)
			.Select(slot => (slot, CalculateF40TestMetrics(plan, slot)))
			.ToList();
		F40TestMetricsResult? layout = results.Count > 0 ? results[0].Metrics : null;
		sb.Append("Slot,Serial,Fixture,FixtureSlot,OffsetV,SpanV,PHOPercentFS,NonLinearMaxPercentFS");
		if (layout != null)
		{
			foreach (F40NamedMetric metric in layout.NonLinearityByTemperature)
			{
				sb.Append(',').Append(Csv("NonLinear_" + metric.Label + "_PercentFS"));
			}
			foreach (F40ThermalMetric metric in layout.ThermalHysteresis)
			{
				sb.Append(',').Append(Csv("THO_" + metric.Label + "_PercentFS"));
			}
			foreach (F40ThermalMetric metric in layout.ThermalHysteresis)
			{
				sb.Append(',').Append(Csv("THS_" + metric.Label + "_PercentFS"));
			}
			foreach (F40ThermalMetric metric in layout.ThermalCoefficients)
			{
				sb.Append(',').Append(Csv("TCO_" + metric.Label + "_PercentFSPerK"));
			}
			foreach (F40ThermalMetric metric in layout.ThermalCoefficients)
			{
				sb.Append(',').Append(Csv("TCS_" + metric.Label + "_PercentFSPerK"));
			}
		}
		sb.AppendLine(",TOPercentFS,TSPercentFS,AccuracyMaxErrorV,AccuracyMaxPercentFS,AccuracyLimitPercentFS,ToleranceV,Result");
		foreach ((F40TestSlotData slot, F40TestMetricsResult metrics) in results)
		{
			sb.Append("Slot").Append(slot.Slot).Append(',')
				.Append(Csv(slot.Serial)).Append(',')
				.Append(Csv(slot.Fixture)).Append(',')
				.Append(Csv(slot.FixtureSlot));
			AppendF40Metric(sb, metrics.OffsetV, plan.ZeroOutputEnabled);
			AppendF40Metric(sb, metrics.SpanV, plan.FullOutputEnabled);
			AppendF40Metric(sb, metrics.PressureHysteresisPercentFs, plan.PressureHysteresisEnabled);
			AppendF40Metric(sb, metrics.MaxNonLinearityPercentFs, plan.NonLinearityEnabled);
			foreach (F40NamedMetric metric in metrics.NonLinearityByTemperature)
			{
				AppendF40Metric(sb, metric.Value, plan.NonLinearityEnabled);
			}
			foreach (F40ThermalMetric metric in metrics.ThermalHysteresis)
			{
				AppendF40Metric(sb, metric.OffsetPercentFs, plan.TemperatureHysteresisEnabled);
			}
			foreach (F40ThermalMetric metric in metrics.ThermalHysteresis)
			{
				AppendF40Metric(sb, metric.SpanPercentFs, plan.TemperatureHysteresisEnabled);
			}
			foreach (F40ThermalMetric metric in metrics.ThermalCoefficients)
			{
				AppendF40Metric(sb, metric.OffsetPercentFs, plan.TemperatureDriftEnabled);
			}
			foreach (F40ThermalMetric metric in metrics.ThermalCoefficients)
			{
				AppendF40Metric(sb, metric.SpanPercentFs, plan.TemperatureDriftEnabled);
			}
			AppendF40Metric(sb, metrics.TotalOffsetPercentFs, plan.TemperatureDriftEnabled);
			AppendF40Metric(sb, metrics.TotalSpanPercentFs, plan.TemperatureDriftEnabled);
			AppendF40Metric(sb, metrics.MaxAccuracyErrorV, plan.AccuracyEnabled);
			AppendF40Metric(sb, metrics.MaxAccuracyPercentFs, plan.AccuracyEnabled);
			AppendF40Metric(sb, plan.AccuracyPercentFs, plan.AccuracyEnabled);
			AppendF40Metric(sb, (double)_testVoltageTolerance.Value, plan.AccuracyEnabled);
			sb.Append(',').AppendLine(GetF40TestResultText(plan, metrics));
		}
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		File.WriteAllText(path, sb.ToString(), Encoding.GetEncoding("GB18030"));
		return path;
	}

	private static void AppendF40Metric(StringBuilder sb, double value, bool enabled)
	{
		sb.Append(',');
		if (enabled)
		{
			sb.Append(FormatCompCell(value));
		}
	}

	private string BuildF40TestOutputDirectory(F40TestPlan plan)
	{
		string root = _testOutputDir.Text.Trim();
		if (string.IsNullOrWhiteSpace(root))
		{
			root = Path.Combine(AppContext.BaseDirectory, "data");
		}
		string model = SanitizeFileName(plan.Model);
		string batch = DateTime.Now.ToString("yyMMddHH", CultureInfo.InvariantCulture) + "_fusion";
		string dir = Path.Combine(root, model, $"{model}-{batch}");
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static string SanitizeFileName(string value)
	{
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			value = value.Replace(c, '_');
		}
		return string.IsNullOrWhiteSpace(value) ? "F40测试" : value;
	}

	private static double AverageOrNaN(IReadOnlyCollection<double> values)
	{
		return values.Count == 0 ? double.NaN : values.Average();
	}

	private void SetF40TestStatus(int slot, string status)
	{
		foreach (DataGridViewRow row in (IEnumerable)_testGrid.Rows)
		{
			if (row.IsNewRow)
			{
				continue;
			}
			Match match = Regex.Match(Convert.ToString(row.Cells["Slot"].Value) ?? "", "\\d+");
			if (match.Success && int.Parse(match.Value, CultureInfo.InvariantCulture) == slot)
			{
				row.Cells["Status"].Value = status;
				break;
			}
		}
		_testGrid.Refresh();
	}

	private async Task BatchReadRawAsync(int startBoard, int startLocalLogicSlot, int totalCount, CancellationToken ct)
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口");
		}
		if (totalCount <= 0)
		{
			throw new InvalidOperationException("采集总工位必须大于0");
		}
		_manualRawGrid.Rows.Clear();
		LogComp($"开始批量读原始02：起始板卡{startBoard}，起始逻辑工位{startLocalLogicSlot}，总工位{totalCount}，{(_useBoardChannel47.Checked ? "使用" : "跳过")}4/7通道");
		for (int i = 0; i < totalCount; i++)
		{
			ct.ThrowIfCancellationRequested();
			BoardSlotTarget target = ResolveBoardSlotFromStart(startBoard, startLocalLogicSlot, i);
			int rowIndex = _manualRawGrid.Rows.Add(i + 1, target.BoardAddr, target.LocalSlot, startLocalLogicSlot + i, "", "", "读取中");
			_manualRawGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIndex - 10);
			_manualRawGrid.Refresh();
			try
			{
				(int BridgeRaw, int TempRaw) raw = ParseRaw02Response(await _board.RequestAsync(target.BoardAddr, 2, new byte[1] { target.LocalSlot }, 13, ct));
				_manualRawGrid.Rows[rowIndex].Cells["Pressure"].Value = raw.BridgeRaw;
				_manualRawGrid.Rows[rowIndex].Cells["Temp"].Value = raw.TempRaw;
				_manualRawGrid.Rows[rowIndex].Cells["Status"].Value = "OK";
				LogComp($"#{i + 1} 板卡{target.BoardAddr} 物理Slot{target.LocalSlot} 原始：压力={raw.BridgeRaw} 温度={raw.TempRaw}");
			}
			catch (Exception ex)
			{
				_manualRawGrid.Rows[rowIndex].Cells["Status"].Value = "失败：" + ex.Message;
				LogComp($"#{i + 1} 板卡{target.BoardAddr} 物理Slot{target.LocalSlot} 读原始失败：{ex.Message}");
			}
		}
		LogComp($"批量读原始02完成：{totalCount}个逻辑工位。");
	}

	private List<CompSlotData> ReadCompensationSlots()
	{
		try
		{
			_compGrid.EndEdit();
		}
		catch
		{
		}
		int num = (int)_compStartSlot.Value;
		int num2 = (int)_compSlotCount.Value;
		List<CompSlotData> list = new List<CompSlotData>();
		foreach (DataGridViewRow item in (IEnumerable)_compGrid.Rows)
		{
			if (item.IsNewRow)
			{
				continue;
			}
			string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
			Match match = Regex.Match(input, "\\d+");
			if (!match.Success)
			{
				continue;
			}
			int num3 = int.Parse(match.Value, CultureInfo.InvariantCulture);
			if (num3 >= num && num3 < num + num2)
			{
				string text = Convert.ToString(item.Cells["Serial"].Value)?.Trim();
				if (string.IsNullOrWhiteSpace(text))
				{
					text = $"{DateTime.Now:yyMMddHH}-9#1-{num3}";
				}
				list.Add(new CompSlotData
				{
					Slot = num3,
					Serial = text
				});
			}
		}
		if (list.Count == 0)
		{
			for (int i = num; i < num + num2; i++)
			{
				list.Add(new CompSlotData
				{
					Slot = i,
					Serial = $"{DateTime.Now:yyMMddHH}-9#1-{i}"
				});
			}
		}
		return list.OrderBy((CompSlotData x) => x.Slot).ToList();
	}

	private List<CompPoint> BuildCompensationPoints()
	{
		double tempDeg = (double)_compT1.Value;
		double tempDeg2 = (double)_compT2.Value;
		double tempDeg3 = (double)_compT3.Value;
		int num = 7;
		List<CompPoint> list = new List<CompPoint>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<CompPoint> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = new CompPoint("T1P1", tempDeg, (double)_compP0.Value, 10.0, 0);
		num2++;
		span[num2] = new CompPoint("T1P2", tempDeg, (double)_compP100.Value, 90.0, 1);
		num2++;
		span[num2] = new CompPoint("T2P1", tempDeg2, (double)_compP0.Value, 10.0, 2);
		num2++;
		span[num2] = new CompPoint("T2P2", tempDeg2, (double)_compP50.Value, 50.0, 3);
		num2++;
		span[num2] = new CompPoint("T2P3", tempDeg2, (double)_compP100.Value, 90.0, 4);
		num2++;
		span[num2] = new CompPoint("T3P1", tempDeg3, (double)_compP0.Value, 10.0, 5);
		num2++;
		span[num2] = new CompPoint("T3P2", tempDeg3, (double)_compP100.Value, 90.0, 6);
		num2++;
		return list;
	}

	private CompTestPlan BuildCompensationTestPlan()
	{
		string model = _compSensorModel.Text.Trim();
		foreach (string item in ResolveCompensationModelIniCandidates(model))
		{
			IniFile ini = IniFile.Load(item);
			string iniUnit = FindIniValue(ini, "压力单位");
			List<double> list = (from x in ReadIndexedIniValues(ini, "测试压力点")
				select TryParseCompPressurePoint(x.Value, iniUnit, out var value) ? (Ok: true, Index: x.Index, Value: value) : (Ok: false, Index: x.Index, Value: 0.0) into x
				where x.Ok
				orderby x.Index
				select x.Value).DistinctBy((double x) => Math.Round(x, 6)).ToList();
			List<double> list2 = (from x in ReadIndexedIniValues(ini, "测试温度点")
				select TryParseNumberFromText(x.Value, out var value) ? (Ok: true, Index: x.Index, Value: value) : (Ok: false, Index: x.Index, Value: 0.0) into x
				where x.Ok
				orderby x.Index
				select x.Value).DistinctBy((double x) => Math.Round(x, 6)).ToList();
			if (list.Count > 0 && list2.Count > 0)
			{
				return new CompTestPlan(list2, list, "model ini " + Path.GetFileName(item));
			}
		}
		double num = (double)_compP0.Value;
		double num2 = (double)_compP50.Value;
		double num3 = (double)_compP100.Value;
		double num4 = num3 - num;
		List<double> pressures = new double[4]
		{
			num,
			num + num4 * 0.25,
			num2,
			num3
		}.DistinctBy((double x) => Math.Round(x, 6)).ToList();
		List<double> temperatures = new double[1] { (double)_compT2.Value }.DistinctBy((double x) => Math.Round(x, 6)).ToList();
		return new CompTestPlan(temperatures, pressures, "screen fallback");
	}

	private IEnumerable<string> ResolveCompensationModelIniCandidates(string model)
	{
		List<string> names = new List<string>();
		if (!string.IsNullOrWhiteSpace(model))
		{
			names.Add(model.Trim() + ".ini");
		}
		if (string.Equals(model, "F40_0.6MPa", StringComparison.OrdinalIgnoreCase))
		{
			names.Add("F40_600KPa.ini");
		}
		IEnumerable<string> dirs = new string[4]
		{
			SettingDir,
			Path.Combine(AppContext.BaseDirectory, "setting"),
			AppContext.BaseDirectory,
			Path.Combine(Environment.CurrentDirectory, "setting")
		}.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string dir in dirs)
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			foreach (string name in names.Distinct<string>(StringComparer.OrdinalIgnoreCase))
			{
				string path = Path.Combine(dir, name);
				if (File.Exists(path))
				{
					yield return path;
				}
			}
			if (string.IsNullOrWhiteSpace(model))
			{
				continue;
			}
			foreach (string item in from x in Directory.GetFiles(dir, "*.ini", SearchOption.TopDirectoryOnly)
				where string.Equals(Path.GetFileNameWithoutExtension(x), model, StringComparison.OrdinalIgnoreCase)
				select x)
			{
				yield return item;
			}
		}
	}

	private static List<(int Index, string Value)> ReadIndexedIniValues(IniFile ini, string keyPrefix)
	{
		List<(int, string)> list = new List<(int, string)>();
		string pattern = "^" + Regex.Escape(keyPrefix) + "\\s+(?<idx>\\d+)\\s*$";
		foreach (string section in ini.Sections)
		{
			foreach (KeyValuePair<string, string> item in ini.Section(section))
			{
				Match match = Regex.Match(item.Key.Trim(), pattern, RegexOptions.IgnoreCase);
				if (match.Success)
				{
					list.Add((int.Parse(match.Groups["idx"].Value, CultureInfo.InvariantCulture), item.Value.Trim()));
				}
			}
		}
		return list;
	}

	private static string? FindIniValue(IniFile ini, string key)
	{
		foreach (string section in ini.Sections)
		{
			foreach (KeyValuePair<string, string> item in ini.Section(section))
			{
				if (string.Equals(item.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))
				{
					return item.Value.Trim();
				}
			}
		}
		return null;
	}

	private bool TryParseCompPressurePoint(string text, string? defaultUnit, out double value)
	{
		value = 0.0;
		if (!TryParseNumberFromText(text, out var value2))
		{
			return false;
		}
		string fromUnit = DetectCompPressureUnit(text, defaultUnit);
		value = ConvertCompPressureUnit(value2, fromUnit, _compPressureUnit.Text);
		return true;
	}

	private static bool TryParseNumberFromText(string text, out double value)
	{
		value = 0.0;
		Match match = Regex.Match(text ?? "", "[+\\-]?\\d+(?:\\.\\d+)?");
		return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	private static string DetectCompPressureUnit(string text, string? fallback)
	{
		string text2 = (text + " " + fallback).ToLowerInvariant();
		if (text2.Contains("psi"))
		{
			return "psi";
		}
		if (text2.Contains("mpa"))
		{
			return "MPa";
		}
		return "kPa";
	}

	private static double ConvertCompPressureUnit(double value, string? fromUnit, string? toUnit)
	{
		double num = (string.Equals(fromUnit, "psi", StringComparison.OrdinalIgnoreCase) ? (value * 6.894757293168361) : (string.Equals(fromUnit, "MPa", StringComparison.OrdinalIgnoreCase) ? (value * 1000.0) : value));
		return string.Equals(toUnit?.Trim(), "psi", StringComparison.OrdinalIgnoreCase) ? (num / 6.894757293168361) : num;
	}

	private double ConvertCompCalibratedPercentToPressure(double percent)
	{
		double num = (double)_compP0.Value;
		double num2 = (double)_compP100.Value;
		return num + (num2 - num) * percent / 100.0;
	}

	private IOvenClient CreateOvenClient()
	{
		return CreateOvenClient(_compOvenModel.Text, GetOvenPrimaryAddress(), _ovenPort.Text);
	}

	private IOvenClient CreateOvenClient(string model, string addressOrIp, string? portText)
	{
		if (IsTcpOvenModel(model))
		{
			if (string.IsNullOrWhiteSpace(addressOrIp))
			{
				throw new InvalidOperationException("烘箱IP不能为空。");
			}
			int result;
			int port = (int.TryParse(portText, out result) ? result : 508);
			byte unitId = byte.TryParse(_ovenUnitId.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedUnitId) ? parsedUnitId : (byte)0;
			return new OvenTcpClient(addressOrIp.Trim(), port, unitId, (int)_timeout.Value, Log);
		}
		int result2;
		int baud = (int.TryParse(_ovenBaud.Text, out result2) ? result2 : 9600);
		int result3;
		int dataBits = (int.TryParse(_ovenDataBits.Text, out result3) ? result3 : 8);
		Parity result4;
		Parity parity = (Enum.TryParse<Parity>(_ovenParity.Text, ignoreCase: true, out result4) ? result4 : Parity.None);
		StopBits stopBits = ((!_ovenStopBits.Text.StartsWith("2")) ? StopBits.One : StopBits.Two);
		return new OvenSerialClient(addressOrIp.Trim(), baud, dataBits, parity, stopBits, (int)_timeout.Value, Log);
	}

	private async Task SetAndHoldOvenAsync(IOvenClient? oven, double targetDeg, CancellationToken ct)
	{
		if (oven == null)
		{
			LogComp($"未启用烘箱自动控制：请人工设置 {targetDeg:0.#}℃，等待 {(int)_compTempHoldSec.Value}s");
			await Task.Delay(TimeSpan.FromSeconds((double)_compTempHoldSec.Value), ct);
			return;
		}
		oven.Write(CommandFor(_compOvenModel.Text, "Open", "POWER,ON"));
		oven.Write(CommandFor(_compOvenModel.Text, "Set", "TEMP,S9999", targetDeg.ToString("0.#", CultureInfo.InvariantCulture)));
		double tol = (double)_compTempTol.Value;
		TimeSpan hold = TimeSpan.FromSeconds((double)_compTempHoldSec.Value);
		DateTime? since = null;
		Stopwatch sw = Stopwatch.StartNew();
		bool waitingLogged = false;
		bool holdingLogged = false;
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			double temp = oven.QueryNumber(CommandFor(_compOvenModel.Text, "Read", "TEMP?"));
			if (Math.Abs(temp - targetDeg) <= tol)
			{
				since.GetValueOrDefault();
				if (!since.HasValue)
				{
					DateTime now = DateTime.Now;
					since = now;
				}
				if (!holdingLogged)
				{
					LogComp($"烘箱稳定{Math.Max(0, (int)Math.Round(hold.TotalSeconds))}s");
					holdingLogged = true;
					waitingLogged = false;
				}
			}
			else
			{
				since = null;
				holdingLogged = false;
				if (!waitingLogged)
				{
					LogComp("烘箱稳定中");
					waitingLogged = true;
				}
			}
			if (hold.TotalSeconds <= 0.0 || (since.HasValue && DateTime.Now - since.Value >= hold))
			{
				LogComp("烘箱稳定完成");
				return;
			}
			if (sw.Elapsed > TimeSpan.FromHours(3.0))
			{
				break;
			}
			await Task.Delay(2000, ct);
		}
		throw new TimeoutException("烘箱稳定超时3小时");
	}

	private async Task SetAndHoldPressureAsync(VisaInstrument? pressure, double targetUserUnit, CancellationToken ct)
	{
		TimeSpan hold = TimeSpan.FromSeconds((double)_compPressureHoldSec.Value);
		if (pressure == null)
		{
			LogComp($"未启用压力控制器：请人工设置 {targetUserUnit:0.###}{_compPressureUnit.Text}，等待 {hold.TotalSeconds:0}s");
			await Task.Delay(hold, ct);
			return;
		}
		double targetKpa = ToCompKpa(targetUserUnit);
		pressure.Write(CommandFor(_pressureModel.Text, "SetPressure", "*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON", targetKpa.ToString("0.######", CultureInfo.InvariantCulture)));
		double tol = (double)_stableTolKpa.Value;
		DateTime? since = null;
		Stopwatch sw = Stopwatch.StartNew();
		bool waitingLogged = false;
		bool holdingLogged = false;
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			double p = pressure.QueryNumber(CommandFor(_pressureModel.Text, "ReadPressure", "*CLS;SENS?"));
			if (Math.Abs(p - targetKpa) <= tol)
			{
				since.GetValueOrDefault();
				if (!since.HasValue)
				{
					DateTime now = DateTime.Now;
					since = now;
				}
				if (!holdingLogged)
				{
					LogComp($"压力稳定{Math.Max(0, (int)Math.Round(hold.TotalSeconds))}s");
					holdingLogged = true;
					waitingLogged = false;
				}
			}
			else
			{
				since = null;
				holdingLogged = false;
				if (!waitingLogged)
				{
					LogComp("压力稳定中");
					waitingLogged = true;
				}
			}
			if (hold.TotalSeconds <= 0.0 || (since.HasValue && DateTime.Now - since.Value >= hold))
			{
				LogComp("压力稳定完成");
				return;
			}
			if (sw.Elapsed > TimeSpan.FromMinutes(10.0))
			{
				break;
			}
			await Task.Delay(1000, ct);
		}
		throw new TimeoutException("压力稳定超时10分钟");
	}

	private Task VentCompPressureAsync(VisaInstrument? pressure, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (pressure == null)
		{
			LogComp("未启用压力控制器：请人工泄压。");
			return Task.CompletedTask;
		}
		pressure.Write(CommandFor(_pressureModel.Text, "Vent", "*CLS;:Sour:Vent 1;:OUTPUT OFF"));
		LogComp("压力控制器泄压完成");
		return Task.CompletedTask;
	}

	private async Task ReturnOvenAndStopAfterTestAsync(IOvenClient? oven, CancellationToken ct)
	{
		double returnTemp = (double)_compT2.Value;
		if (oven == null)
		{
			LogComp($"未启用烘箱自动控制：请人工返回 {returnTemp:0.###}℃，约4分钟后关闭烘箱。");
			return;
		}
		oven.Write(CommandFor(_compOvenModel.Text, "Open", "POWER,ON"));
		oven.Write(CommandFor(_compOvenModel.Text, "Set", "TEMP,S9999", returnTemp.ToString("0.#", CultureInfo.InvariantCulture)));
		LogComp($"测试完成，烘箱返回{returnTemp:0.###}℃，等待约4分钟后关闭");
		await Task.Delay(TimeSpan.FromMinutes(4.0), ct);
		oven.Write(CommandFor(_compOvenModel.Text, "Stop", "POWER,OFF"));
		LogComp("烘箱已关闭");
	}

	private async Task TryEmergencyCompensationShutdownAsync(VisaInstrument? pressure, IOvenClient? oven)
	{
		try
		{
			await VentCompPressureAsync(pressure, CancellationToken.None);
		}
		catch (Exception ex)
		{
			LogComp("异常收尾泄压失败：" + ex.Message);
		}
		if (oven == null)
		{
			return;
		}
		try
		{
			oven.Write(CommandFor(_compOvenModel.Text, "Stop", "POWER,OFF"));
			LogComp("异常收尾已发送烘箱停止命令");
		}
		catch (Exception ex2)
		{
			LogComp("异常收尾停止烘箱失败：" + ex2.Message);
		}
	}

	private async Task RunCompPressureZeroAsync(VisaInstrument? pressure, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (pressure == null)
		{
			LogComp("已勾选调零：当前未启用压力控制器，请先人工调零后继续。");
			await Task.Delay(500, ct);
			return;
		}
		try
		{
			pressure.Write(CommandFor(_pressureModel.Text, "SetGaug", "*CLS"));
			await Task.Delay(500, ct);
		}
		catch
		{
		}
		pressure.Write(CommandFor(_pressureModel.Text, "ZeroCheck", "*CLS"));
		LogComp("表压压力控制器调零命令已发送");
		await Task.Delay(2000, ct);
	}

	private async Task<string?> AutoConfigureCompensationAsync(List<CompSlotData> slots, VisaInstrument? pressure, CancellationToken ct)
	{
		List<CompConfigCandidate> candidates = LoadCompensationConfigCandidates();
		if (candidates.Count == 0)
		{
			LogComp("未找到历史配置数据候选，自动配置改为仅检验当前配置。");
			Dictionary<int, CompVerifyResult> verifyCurrent = await VerifyCompensationBatchAsync(slots, pressure, "当前配置", ct);
			foreach (CompSlotData slot in slots)
			{
				if (verifyCurrent.TryGetValue(slot.Slot, out CompVerifyResult item))
				{
					ApplyCompVerifyResult(slot, item, string.IsNullOrWhiteSpace(slot.AppliedConfig) ? "CC050300" : slot.AppliedConfig);
				}
				item = null;
			}
			return SaveCompensationConfigCsv(slots);
		}
		LogComp($"开始采集20%配置：候选 {candidates.Count} 组 -> {string.Join(", ", candidates.Select((CompConfigCandidate x) => x.Register8))}");
		List<CompSlotData> pending = slots.ToList();
		Dictionary<int, (CompConfigCandidate Candidate, CompVerifyResult Verify, double Score)> best = new Dictionary<int, (CompConfigCandidate, CompVerifyResult, double)>();
		foreach (CompConfigCandidate candidate in candidates)
		{
			ct.ThrowIfCancellationRequested();
			if (pending.Count == 0)
			{
				break;
			}
			LogComp($"开始配置 {candidate.Register8}，待检验 {pending.Count} 个工位");
			byte[] config4 = ParseConfigRegisterPair(candidate.RegA, candidate.RegB);
			foreach (CompSlotData slot2 in pending)
			{
				ct.ThrowIfCancellationRequested();
				if (!string.Equals(slot2.AppliedConfig, candidate.Register8, StringComparison.OrdinalIgnoreCase))
				{
					BoardSlotTarget target = ResolveBoardSlot(slot2.Slot);
					await _board.WriteConfigAsync(target.BoardAddr, target.LocalSlot, "0304", config4, ct);
					LogComp($"GlobalSlot{slot2.Slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 配置 {candidate.Register8}");
				}
				slot2.AppliedConfig = candidate.Register8;
				SetCompStatus(slot2.Slot, "已写" + candidate.Register8);
			}
			Dictionary<int, CompVerifyResult> results = await VerifyCompensationBatchAsync(pending, pressure, candidate.Register8, ct);
			int passedThisRound = 0;
			foreach (CompSlotData slot3 in pending.ToList())
			{
				if (results.TryGetValue(slot3.Slot, out CompVerifyResult verify))
				{
					ApplyCompVerifyResult(slot3, verify, candidate.Register8);
					double score = ScoreCompVerifyResult(verify);
					if (!best.TryGetValue(slot3.Slot, out var old) || score < old.Score)
					{
						best[slot3.Slot] = (candidate, verify, score);
					}
					if (verify.PassAll)
					{
						slot3.ConfigPassed = true;
						passedThisRound++;
						pending.Remove(slot3);
						SetCompStatus(slot3.Slot, "配置通过 " + candidate.Register8);
					}
					else
					{
						SetCompStatus(slot3.Slot, $"{candidate.Register8} {(verify.Pass20 ? "20√" : "20×")} {(verify.Pass80 ? "80√" : "80×")} {(verify.Pass60 ? "60√" : "60×")}");
					}
					verify = null;
					old = default((CompConfigCandidate, CompVerifyResult, double));
				}
			}
			LogComp($"配置 {candidate.Register8} 检验完成：本轮通过 {passedThisRound}，剩余 {pending.Count}");
		}
		foreach (CompSlotData slot4 in pending)
		{
			ct.ThrowIfCancellationRequested();
			if (!best.TryGetValue(slot4.Slot, out var choice))
			{
				SetCompStatus(slot4.Slot, "配置筛选失败");
				continue;
			}
			if (!string.Equals(slot4.AppliedConfig, choice.Candidate.Register8, StringComparison.OrdinalIgnoreCase))
			{
				BoardSlotTarget target2 = ResolveBoardSlot(slot4.Slot);
				await _board.WriteConfigAsync(target2.BoardAddr, target2.LocalSlot, "0304", ParseConfigRegisterPair(choice.Candidate.RegA, choice.Candidate.RegB), ct);
				LogComp($"GlobalSlot{slot4.Slot} -> 回写最优配置 {choice.Candidate.Register8}");
			}
			ApplyCompVerifyResult(slot4, choice.Verify, choice.Candidate.Register8);
			slot4.ConfigPassed = false;
			SetCompStatus(slot4.Slot, "未全合格，取最优 " + choice.Candidate.Register8);
			LogComp($"Slot{slot4.Slot} 未找到完全合格配置，最优={choice.Candidate.Register8} 20={choice.Verify.P20:0.###} 80={choice.Verify.P80:0.###} 60={choice.Verify.P60:0.###}");
			choice = default((CompConfigCandidate, CompVerifyResult, double));
		}
		LogComp($"20%配置筛选完成：合格 {slots.Count((CompSlotData x) => x.ConfigPassed)} / {slots.Count}");
		return SaveCompensationConfigCsv(slots);
	}

	private async Task<Dictionary<int, CompVerifyResult>> VerifyCompensationBatchAsync(List<CompSlotData> slots, VisaInstrument? pressure, string tag, CancellationToken ct)
	{
		Dictionary<int, CompVerifyResult> result = new Dictionary<int, CompVerifyResult>();
		if (slots.Count == 0)
		{
			return result;
		}
		LogComp(tag + "：开始检验_压力点1");
		await SetAndHoldPressureAsync(pressure, (double)_compP0.Value, ct);
		Dictionary<int, CompVerifySnapshot> low = await ReadCompensationCalibratedBatchAsync(slots, "压力点1", ct);
		LogComp(tag + "：开始检验_压力点3");
		await SetAndHoldPressureAsync(pressure, (double)_compP100.Value, ct);
		Dictionary<int, CompVerifySnapshot> high = await ReadCompensationCalibratedBatchAsync(slots, "压力点3", ct);
		double refTemp = (double)_compT2.Value;
		foreach (CompSlotData slot in slots)
		{
			if (low.TryGetValue(slot.Slot, out CompVerifySnapshot p1) && high.TryGetValue(slot.Slot, out CompVerifySnapshot p3))
			{
				double p20 = p1.PressurePercent;
				double p80 = p3.PressurePercent;
				double p81 = p80 - p20;
				double pressureAcc = Math.Max(Math.Abs(p20 - 20.0), Math.Abs(p80 - 80.0)) * 10.0;
				double tempAcc = Math.Max(Math.Abs(p1.TempDeg - refTemp), Math.Abs(p3.TempDeg - refTemp));
				result[slot.Slot] = new CompVerifyResult(p20, p80, p81, p1.TempDeg, p3.TempDeg, pressureAcc, tempAcc, p1.Valid, p3.Valid);
				p1 = null;
				p3 = null;
			}
		}
		LogComp($"{tag}：20%合格 {result.Values.Count((CompVerifyResult x) => x.Pass20)}，80%合格 {result.Values.Count((CompVerifyResult x) => x.Pass80)}，60%合格 {result.Values.Count((CompVerifyResult x) => x.Pass60)}，均合格 {result.Values.Count((CompVerifyResult x) => x.PassAll)}");
		return result;
	}

	private async Task<Dictionary<int, CompVerifySnapshot>> ReadCompensationCalibratedBatchAsync(List<CompSlotData> slots, string pointName, CancellationToken ct)
	{
		Dictionary<int, CompVerifySnapshot> result = new Dictionary<int, CompVerifySnapshot>();
		foreach (CompSlotData slot in slots)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				BoardSlotTarget target = ResolveBoardSlot(slot.Slot);
				(double PressurePercent, double TempDeg, bool Valid) cal = ParseCalibrated12Response(await _board.RequestAsync(target.BoardAddr, 18, new byte[1] { target.LocalSlot }, 13, ct));
				result[slot.Slot] = new CompVerifySnapshot(cal.PressurePercent, cal.TempDeg, cal.Valid);
				LogComp($"GlobalSlot{slot.Slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} {pointName} 0x12：P={cal.PressurePercent:0.######}% T={cal.TempDeg:0.######}℃ {(cal.Valid ? "OK" : "INVALID")}");
			}
			catch (Exception ex)
			{
				LogComp($"Slot{slot.Slot} {pointName} 0x12读取失败：{ex.Message}");
			}
		}
		return result;
	}

	private double ToCompKpa(double value)
	{
		return string.Equals(_compPressureUnit.Text, "psi", StringComparison.OrdinalIgnoreCase) ? (value * 6.894757293168361) : value;
	}

	private static (double PressurePercent, double TempDeg, bool Valid) ParseCalibrated12Response(byte[] rsp)
	{
		if (rsp.Length < 11 || rsp[1] != 18)
		{
			throw new InvalidDataException("0x12响应长度/功能码异常：" + Hex(rsp));
		}
		byte[] b = rsp.Skip(3).Take(8).ToArray();
		uint num = ReadUInt32BE(b, 0);
		uint num2 = ReadUInt32BE(b, 4);
		double item = ((num == uint.MaxValue) ? 25599.999994 : ((double)num * 100.0 / 16777215.0));
		double item2 = ((num2 == uint.MaxValue) ? 25599.999994 : ((double)num2 * 66.0 / 16777215.0 - 1.0));
		bool item3 = num != uint.MaxValue && num2 != uint.MaxValue;
		return (PressurePercent: item, TempDeg: item2, Valid: item3);
	}

	private static (int BridgeRaw, int TempRaw) ParseRaw02Response(byte[] rsp)
	{
		if (rsp.Length < 11 || rsp[1] != 2)
		{
			throw new InvalidDataException("0x02响应长度/功能码异常：" + Hex(rsp));
		}
		byte[] b = rsp.Skip(3).Take(8).ToArray();
		return (BridgeRaw: ReadInt32BE(b, 0), TempRaw: ReadInt32BE(b, 4));
	}

	private static int ReadInt32BE(byte[] b, int offset)
	{
		return (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
	}

	private static uint ReadUInt32BE(byte[] b, int offset)
	{
		return (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
	}

	private string SaveCompensationCsv(List<CompSlotData> slots, List<CompPoint> points)
	{
		string text = _compSensorModel.Text.Trim();
		Match match = Regex.Match(text, "F40[_\\-]?(.+)$", RegexOptions.IgnoreCase);
		string value = (match.Success ? match.Groups[1].Value : text);
		string value2 = "表压";
		string path = $"F40_{value}_{value2}_原始数据{DateTime.Now:yyMMddHHmm}.csv";
		string text2 = Path.Combine(_compOutputDir.Text.Trim(), path);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("SerialNo,SlotNo,TestResult,P0,P100,PUnit,MinT,MaxT");
		foreach (CompPoint item in points.OrderBy((CompPoint x) => x.Index))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(48, 4, stringBuilder2);
			handler.AppendLiteral(",BridgeRaw_");
			handler.AppendFormatted(item.Name);
			handler.AppendLiteral(",BridgeDesired_");
			handler.AppendFormatted(item.Name);
			handler.AppendLiteral(",TempRaw_");
			handler.AppendFormatted(item.Name);
			handler.AppendLiteral(",TempDesired_");
			handler.AppendFormatted(item.Name);
			stringBuilder3.Append(ref handler);
		}
		stringBuilder.AppendLine(",coefficients,,,,,,,,,,");
		foreach (CompSlotData item2 in slots.OrderBy((CompSlotData x) => x.Slot))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(13, 8, stringBuilder2);
			handler.AppendLiteral("Slot");
			handler.AppendFormatted(item2.Slot);
			handler.AppendLiteral(",");
			handler.AppendFormatted(Csv(item2.Serial));
			handler.AppendLiteral(",");
			handler.AppendFormatted(item2.Ok ? 1 : 0);
			handler.AppendLiteral(",");
			handler.AppendFormatted(_compP0.Value.ToString(CultureInfo.InvariantCulture));
			handler.AppendLiteral(",");
			handler.AppendFormatted(_compP100.Value.ToString(CultureInfo.InvariantCulture));
			handler.AppendLiteral(",");
			handler.AppendFormatted(_compPressureUnit.Text);
			handler.AppendLiteral(",");
			handler.AppendFormatted(_compT1.Value.ToString(CultureInfo.InvariantCulture));
			handler.AppendLiteral("℃,");
			handler.AppendFormatted(_compT3.Value.ToString(CultureInfo.InvariantCulture));
			handler.AppendLiteral("℃");
			stringBuilder4.Append(ref handler);
			for (int num = 0; num < 7; num++)
			{
				stringBuilder.AppendFormat(CultureInfo.InvariantCulture, ",{0},{1},{2},{3}", item2.BridgeRaw[num], item2.BridgeDesired[num], item2.TempRaw[num], item2.TempDesired[num]);
			}
			for (int num2 = 0; num2 < 10; num2++)
			{
				stringBuilder.Append(',').Append((item2.Coefficients.Length > num2) ? item2.Coefficients[num2].ToString(CultureInfo.InvariantCulture) : "0");
			}
			stringBuilder.AppendLine(",0");
		}
		File.WriteAllText(text2, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return text2;
	}

	private string? SaveCompensationConfigCsv(List<CompSlotData> slots)
	{
		string text = ResolveCompConfigDataDirectory(createIfMissing: true);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		Directory.CreateDirectory(text);
		string path = $"{_compSensorModel.Text.Trim()}_表压_配置数据{DateTime.Now:yyMMddHHmm}.csv";
		string text2 = Path.Combine(text, path);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("工位号,序列号,夹具,夹具工位号,20%,80%,60%,Gain,IOFFSC,ADC,寄存器,,,");
		foreach (CompSlotData item in slots.OrderBy((CompSlotData x) => x.Slot))
		{
			string text3 = (item.AppliedConfig ?? "").Trim().ToUpperInvariant();
			var (value, value2, value3) = DecodeCompRegister(text3);
			stringBuilder.Append("Slot").Append(item.Slot).Append(',')
				.Append(Csv(item.Serial))
				.Append(',')
				.Append(Csv(GetCompGridCellText(item.Slot, "Fixture")))
				.Append(',')
				.Append(Csv(GetCompGridCellText(item.Slot, "FixtureSlot")))
				.Append(',')
				.Append(FormatCompCell(item.P20))
				.Append(',')
				.Append(FormatCompCell(item.P80))
				.Append(',')
				.Append(FormatCompCell(item.P60))
				.Append(',')
				.Append(value)
				.Append(',')
				.Append(value2)
				.Append(',')
				.Append(value3)
				.Append(',')
				.Append(text3)
				.AppendLine(",,,");
		}
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		File.WriteAllText(text2, stringBuilder.ToString(), Encoding.GetEncoding("GB18030"));
		return text2;
	}

	private string SaveCompensationTestCsv(List<CompTestMeasurement> rows, double? temperature = null)
	{
		string text = _compOutputDir.Text.Trim();
		Directory.CreateDirectory(text);
		string value = (string.IsNullOrWhiteSpace(_compSensorModel.Text) ? "F40" : _compSensorModel.Text.Trim());
		string tempTag = temperature.HasValue ? $"_{temperature.Value:0.###}C" : "";
		string path = $"{value}_表压_测试数据{tempTag}_{DateTime.Now:yyMMddHHmmss}.csv";
		string text2 = Path.Combine(text, path);
		double num = Math.Abs((double)_compP100.Value - (double)_compP0.Value);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("Slot,Serial,SetTempC,SetPressure,PressureUnit,ReadPressure,ReadTempC,PressureError,PressureErrorPermille,TempErrorC,PressurePass,TempPass,Valid");
		foreach (CompTestMeasurement item in from x in rows
			orderby x.SetTemp, x.SetPressure, x.Slot
			select x)
		{
			double value2 = ((num <= 0.0 || double.IsNaN(item.PressureError)) ? double.NaN : (item.PressureError / num * 1000.0));
			stringBuilder.Append("Slot").Append(item.Slot).Append(',')
				.Append(Csv(item.Serial))
				.Append(',')
				.Append(FormatCompCell(item.SetTemp))
				.Append(',')
				.Append(FormatCompCell(item.SetPressure))
				.Append(',')
				.Append(Csv(_compPressureUnit.Text))
				.Append(',')
				.Append(FormatCompCell(item.ReadPressure))
				.Append(',')
				.Append(FormatCompCell(item.ReadTemp))
				.Append(',')
				.Append(FormatCompCell(item.PressureError))
				.Append(',')
				.Append(FormatCompCell(value2))
				.Append(',')
				.Append(FormatCompCell(item.TempError))
				.Append(',')
				.Append(item.PressurePass ? "1" : "0")
				.Append(',')
				.Append(item.TempPass ? "1" : "0")
				.Append(',')
				.Append(item.Valid ? "1" : "0")
				.AppendLine();
		}
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		File.WriteAllText(text2, stringBuilder.ToString(), Encoding.GetEncoding("GB18030"));
		return text2;
	}

	private static string Csv(string value)
	{
		return (value.Contains(',') || value.Contains('"')) ? ("\"" + value.Replace("\"", "\"\"") + "\"") : value;
	}

	private static string FormatCompCell(double value)
	{
		return (double.IsNaN(value) || double.IsInfinity(value)) ? "" : value.ToString("0.######", CultureInfo.InvariantCulture);
	}

	private static (string Gain, string IOFFSC, string ADC) DecodeCompRegister(string reg)
	{
		if (reg == null)
		{
			reg = "";
		}
		if (!Regex.IsMatch(reg, "^[0-9A-Fa-f]{8}$"))
		{
			return (Gain: "", IOFFSC: "", ADC: "");
		}
		int value = Math.Max(0, Convert.ToInt32(reg.Substring(0, 1), 16) - 8);
		return (Gain: "00" + reg.Substring(2, 2).ToUpperInvariant(), IOFFSC: "00" + reg.Substring(6, 2).ToUpperInvariant(), ADC: $"{value:X}000");
	}

	private string ResolveCompConfigDataDirectory(bool createIfMissing)
	{
		string text = _compOutputDir.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		List<string> list = new List<string>();
		try
		{
			if (Directory.Exists(text))
			{
				list.Add(Path.Combine(text, "配置数据"));
				string text2 = Directory.GetParent(text)?.FullName;
				if (!string.IsNullOrWhiteSpace(text2))
				{
					list.Add(Path.Combine(text2, "配置数据"));
				}
			}
			for (DirectoryInfo directoryInfo = new DirectoryInfo(text); directoryInfo != null; directoryInfo = directoryInfo.Parent)
			{
				list.Add(Path.Combine(directoryInfo.FullName, "配置数据"));
			}
		}
		catch
		{
		}
		string text3 = list.FirstOrDefault(Directory.Exists);
		if (!string.IsNullOrWhiteSpace(text3))
		{
			return text3;
		}
		if (createIfMissing)
		{
			string text4 = Directory.GetParent(text)?.FullName;
			if (!string.IsNullOrWhiteSpace(text4))
			{
				return Path.Combine(text4, "配置数据");
			}
		}
		return "";
	}

	private List<CompConfigCandidate> LoadCompensationConfigCandidates()
	{
		string text = ResolveCompConfigDataDirectory(createIfMissing: false);
		if (string.IsNullOrWhiteSpace(text) || !Directory.Exists(text))
		{
			return new List<CompConfigCandidate>();
		}
		string text2 = _compSensorModel.Text.Trim();
		string[] files = Directory.GetFiles(text, text2 + "_*配置数据*.csv", SearchOption.TopDirectoryOnly);
		List<(string Reg, double P20, double P80, double P60)> list = new List<(string Reg, double P20, double P80, double P60)>();
		string[] array = files;
		foreach (string path in array)
		{
			foreach (string item2 in File.ReadAllLines(path, DetectTextEncoding(path)).Skip(1))
			{
				if (string.IsNullOrWhiteSpace(item2))
				{
					continue;
				}
				string[] array2 = SplitSimpleCsv(item2);
				if (array2.Length >= 11)
				{
					string text3 = array2[10].Trim().ToUpperInvariant();
					if (Regex.IsMatch(text3, "^[0-9A-F]{8}$") && double.TryParse(array2[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.TryParse(array2[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) && double.TryParse(array2[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
					{
						list.Add((text3, result, result2, result3));
					}
				}
			}
		}
		List<CompConfigCandidate> list2 = (from x in (from x in list
				group x by x.Reg).Select(delegate(IGrouping<string, (string Reg, double P20, double P80, double P60)> g)
			{
				double avgP = g.Average(((string Reg, double P20, double P80, double P60) x) => x.P20);
				double avgP2 = g.Average(((string Reg, double P20, double P80, double P60) x) => x.P80);
				double avgP3 = g.Average(((string Reg, double P20, double P80, double P60) x) => x.P60);
				int num2 = g.Count(((string Reg, double P20, double P80, double P60) x) => x.P20 < 1000.0 && x.P80 < 1000.0);
				int num3 = g.Count(((string Reg, double P20, double P80, double P60) x) => x.P20 >= 15.0 && x.P20 <= 25.0 && x.P80 >= 80.0 && x.P80 <= 85.0 && x.P60 >= 60.0);
				return new CompConfigCandidate(g.Key, g.Key.Substring(0, 4), g.Key.Substring(4, 4), avgP, avgP2, avgP3, (g.Count() == 0) ? 0.0 : ((double)num3 / (double)g.Count()), (g.Count() == 0) ? 0.0 : ((double)num2 / (double)g.Count()), g.Count());
			})
			orderby x.PassRate descending, x.ValidRate descending, Math.Abs(x.AvgP20 - 20.0) + Math.Abs(x.AvgP80 - 82.5) + Math.Abs(x.AvgP60 - 60.0), x.SampleCount descending
			select x).ToList();
		int num = list2.FindIndex((CompConfigCandidate x) => string.Equals(x.Register8, "CC050300", StringComparison.OrdinalIgnoreCase));
		if (num > 0)
		{
			CompConfigCandidate item = list2[num];
			list2.RemoveAt(num);
			list2.Insert(0, item);
		}
		return list2;
	}

	private static Encoding DetectTextEncoding(string path)
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		byte[] bytes = File.ReadAllBytes(path);
		try
		{
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		}
		catch
		{
			return Encoding.GetEncoding("GB18030");
		}
	}

	private static string[] SplitSimpleCsv(string line)
	{
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			switch (c)
			{
			case '"':
				if (flag && i + 1 < line.Length && line[i + 1] == '"')
				{
					stringBuilder.Append('"');
					i++;
				}
				else
				{
					flag = !flag;
				}
				continue;
			case ',':
				if (!flag)
				{
					list.Add(stringBuilder.ToString());
					stringBuilder.Clear();
					continue;
				}
				break;
			}
			stringBuilder.Append(c);
		}
		list.Add(stringBuilder.ToString());
		return list.ToArray();
	}

	private void SetCompStatus(int slot, string status)
	{
		foreach (DataGridViewRow item in (IEnumerable)_compGrid.Rows)
		{
			if (!item.IsNewRow)
			{
				string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
				Match match = Regex.Match(input, "\\d+");
				if (match.Success && int.Parse(match.Value, CultureInfo.InvariantCulture) == slot)
				{
					item.Cells["Status"].Value = status;
					_compGrid.Refresh();
					break;
				}
			}
		}
	}

	private void ApplyCompVerifyResult(CompSlotData slot, CompVerifyResult verify, string register8)
	{
		slot.AppliedConfig = register8;
		slot.P20 = verify.P20;
		slot.P80 = verify.P80;
		slot.P60 = verify.P60;
		slot.PressureAccuracyPermille = verify.PressureAccuracyPermille;
		slot.TempAccuracyDeg = verify.TempAccuracyDeg;
		slot.ConfigPassed = verify.PassAll;
		foreach (DataGridViewRow item in (IEnumerable)_compGrid.Rows)
		{
			if (!item.IsNewRow)
			{
				string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
				Match match = Regex.Match(input, "\\d+");
				if (match.Success && int.Parse(match.Value, CultureInfo.InvariantCulture) == slot.Slot)
				{
					item.Cells["P20"].Value = FormatCompCell(slot.P20);
					item.Cells["P80"].Value = FormatCompCell(slot.P80);
					item.Cells["P60"].Value = FormatCompCell(slot.P60);
					item.Cells["PressureAcc"].Value = FormatCompCell(slot.PressureAccuracyPermille);
					item.Cells["TempAcc"].Value = FormatCompCell(slot.TempAccuracyDeg);
					item.Cells["Status"].Value = (verify.PassAll ? ("配置通过 " + register8) : $"{register8} {(verify.Pass20 ? "20√" : "20×")} {(verify.Pass80 ? "80√" : "80×")} {(verify.Pass60 ? "60√" : "60×")}");
					_compGrid.Refresh();
					break;
				}
			}
		}
	}

	private static double ScoreCompVerifyResult(CompVerifyResult verify)
	{
		double num = ((verify.P20 < 15.0) ? (15.0 - verify.P20) : ((verify.P20 > 25.0) ? (verify.P20 - 25.0) : 0.0));
		double num2 = ((verify.P80 < 80.0) ? (80.0 - verify.P80) : ((verify.P80 > 85.0) ? (verify.P80 - 85.0) : 0.0));
		double num3 = ((verify.P60 < 60.0) ? (60.0 - verify.P60) : 0.0);
		int num4 = ((!verify.Valid) ? 100000 : 0);
		return (double)num4 + num * 5.0 + num2 * 5.0 + num3 * 8.0 + Math.Abs(verify.P20 - 20.0) + Math.Abs(verify.P80 - 82.5);
	}

	private string GetCompGridCellText(int slot, string columnName)
	{
		foreach (DataGridViewRow item in (IEnumerable)_compGrid.Rows)
		{
			if (!item.IsNewRow)
			{
				string input = Convert.ToString(item.Cells["Slot"].Value) ?? "";
				Match match = Regex.Match(input, "\\d+");
				if (match.Success && int.Parse(match.Value, CultureInfo.InvariantCulture) == slot)
				{
					return Convert.ToString(item.Cells[columnName].Value) ?? "";
				}
			}
		}
		return "";
	}

	private void LogComp(string message)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				LogComp(message);
			});
			return;
		}
		string text = $"[{DateTime.Now:HH:mm:ss}] {message}";
		_logComp.AppendText(text + Environment.NewLine);
		_logCompManual.AppendText(text + Environment.NewLine);
		Log(message, important: true);
	}

	private void LogTest(string message)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				LogTest(message);
			});
			return;
		}
		string text = $"[{DateTime.Now:HH:mm:ss}] {message}";
		_logTest.AppendText(text + Environment.NewLine);
		Log("[测试] " + message, important: true);
	}

	private static Label Pill(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			Padding = new Padding(12, 5, 12, 5),
			Margin = new Padding(5, 0, 5, 0),
			BackColor = IndustrialSurfaceAlt,
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
	}

	private MenuStrip BuildMenu()
	{
		MenuStrip menuStrip = new MenuStrip
		{
			BackColor = Color.White,
			ForeColor = Color.FromArgb(30, 41, 59)
		};
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("文件(&F)");
		toolStripMenuItem.DropDownItems.Add("选择原始CSV", null, delegate
		{
			_browse.PerformClick();
		});
		toolStripMenuItem.DropDownItems.Add("重新加载CSV", null, delegate
		{
			_loadCsv.PerformClick();
		});
		toolStripMenuItem.DropDownItems.Add("打开日志目录", null, delegate
		{
			OpenLogDirectory();
		});
		toolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
		toolStripMenuItem.DropDownItems.Add("退出", null, delegate
		{
			Close();
		});
		ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem("设备(&D)");
		toolStripMenuItem2.DropDownItems.Add("刷新串口", null, delegate
		{
			RefreshPorts();
		});
		toolStripMenuItem2.DropDownItems.Add("打开/关闭板卡", null, delegate
		{
			ToggleSerial();
		});
		toolStripMenuItem2.DropDownItems.Add("AA通信测试", null, async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(170, Array.Empty<byte>(), 4, ct);
			});
		});
		ToolStripMenuItem toolStripMenuItem3 = new ToolStripMenuItem("运行(&R)");
		toolStripMenuItem3.DropDownItems.Add("只计算选中", null, delegate
		{
			_calcSelected.PerformClick();
		});
		toolStripMenuItem3.DropDownItems.Add("只写选中系数", null, async delegate
		{
			await SafeRunAsync(WriteSelectedAsync);
		});
		toolStripMenuItem3.DropDownItems.Add("开始自动标定", null, async delegate
		{
			await SafeRunAsync(AutoCalibrateAsync);
		});
		toolStripMenuItem3.DropDownItems.Add("停止", null, delegate
		{
			StopCurrentRun();
		});
		ToolStripMenuItem toolStripMenuItem4 = new ToolStripMenuItem("帮助(&H)");
		toolStripMenuItem4.DropDownItems.Add("检测更新", null, async delegate
		{
			await CheckForUpdatesAsync();
		});
		toolStripMenuItem4.DropDownItems.Add("关于", null, delegate
		{
			MessageBox.Show(this, $"软件补偿与F40标定\r\nv{AppUpdateService.CurrentVersionText}\r\nC# 重构版 · win-x86 · 自带 .NET 8", "关于");
		});
		menuStrip.Items.AddRange(new ToolStripItem[4] { toolStripMenuItem, toolStripMenuItem2, toolStripMenuItem3, toolStripMenuItem4 });
		return menuStrip;
	}

	private TabPage BuildPlanTab()
	{
		TabPage tabPage = new TabPage("方案")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		GroupBox groupBox = Card("测试方案 / Project");
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(14),
			WrapContents = true
		};
		Add(flowLayoutPanel, "方案名称", _planName);
		Add(flowLayoutPanel, "原始CSV", _csvPath, _browse, _loadCsv);
		Add(flowLayoutPanel, "压力点", "P0", _p0, "Pmid", _pmid, "Pfull", _pfull, "单位", _pressureUnit);
		Add(flowLayoutPanel, "输出目标", "低V", _calOutputMinV, "满V", _calOutputMaxV, "低%", _calPercentMin, "满%", _calPercentMax, "容差V", _calVoltageTolerance, _calLinearityEnabled);
		groupBox.Controls.Add(flowLayoutPanel);
		GroupBox groupBox2 = Card("快速动作");
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(16),
			FlowDirection = FlowDirection.TopDown
		};
		Button[] array = new Button[7] { _selectValid, _selectDaq60, _selectStableF40Slots, _copyRawDataMap, _selectAll, _selectNone, _start };
		foreach (Button button in array)
		{
			button.Width = 180;
			button.Height = 32;
			flowLayoutPanel2.Controls.Add(button);
		}
		groupBox2.Controls.Add(flowLayoutPanel2);
		GroupBox groupBox3 = Card("运行逻辑");
		groupBox3.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			BackColor = Color.White,
			Font = new Font("Microsoft YaHei UI", 10f),
			Text = "1. 读取原始补偿CSV，默认固定加载现场可用的4个通道32工位：1-8、9-16、17-24、33-40。\r\n2. 可用“复用原始数据”把后续Slot的原补偿数据复制到这32个可写工位。\r\n3. 根据多DAQ配置和“手动通道覆盖”计算每个工位的DAQ地址和采集通道。\r\n4. 控制压力到低点/满点，DAQ/DMM读取输出电压并修正BridgeDesired百分比。\r\n5. 调用CalibrationL6.dll生成10个Int32系数。\r\n6. 板卡按现场验证策略写入：0x63进写系数固定Slot1 -> 0x11写目标Slot -> 0x61退出固定Slot1。\r\n7. 支持逐工位闭环，也支持统一加压批量扫表；写后按原方案复测低点/满点/中点线性。"
		});
		GroupBox groupBox4 = Card("关键配置位置");
		groupBox4.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			BackColor = Color.White,
			Text = "配置目录：" + SettingDir + "\r\nSetting.ini：设备、串口、GPIB、多DAQ、标定参数\r\nCommand.ini：压控/DAQ/DMM指令模板\r\nlogs：自动保存运行日志\r\n\r\n本程序只保留F40标定所需的设备配置、指令模板、工位运行和日志页面。"
		});
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tableLayoutPanel.Controls.Add(groupBox3, 0, 1);
		tableLayoutPanel.Controls.Add(groupBox4, 1, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildParameterTab()
	{
		TabPage tabPage = new TabPage("参数控制")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 1
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3
		};
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Absolute, 175f));
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Absolute, 145f));
		tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		GroupBox groupBox = Card("接口设置 Interface Settings");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			Padding = new Padding(12)
		};
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false
		};
		Add(flowLayoutPanel, "数据采集卡/板卡 Port", _com, "波特率 Baud", _boardBaud, "数据位 DataBits", _boardDataBits, "校验位 Parity", _boardParity, "停止位 StopBits", _boardStopBits);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false
		};
		Add(flowLayoutPanel2, "烘箱IP", _ovenIp, "端口 Port", _ovenPort, "备用串口 Port", _ovenCom, "波特率 Baud", _ovenBaud, "数据位 DataBits", _ovenDataBits, "校验位 Parity", _ovenParity, "停止位 StopBits", _ovenStopBits);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel3.Controls.Add(flowLayoutPanel2, 1, 0);
		groupBox.Controls.Add(tableLayoutPanel3);
		GroupBox groupBox2 = Card("压力控制器 Pressure Controller");
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12),
			WrapContents = true
		};
		Add(flowLayoutPanel3, _useGpib, "GPIB地址", _pressureGpibAddress, "GPIB端口", _pressureGpibPort, "型号", _pressureModel, "VISA", _pressureAddr);
		groupBox2.Controls.Add(flowLayoutPanel3);
		GroupBox groupBox3 = Card("多台 DAQ973A 配置");
		FlowLayoutPanel flowLayoutPanel4 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12),
			WrapContents = true
		};
		Add(flowLayoutPanel4, "DMM Model", _dmmModel, "GPIB地址", _dmmGpibAddress, "GPIB端口", _dmmGpibPort, "VISA", _dmmAddr);
		Add(flowLayoutPanel4, _useDaqChannel, _multiDaq, "默认映射", _channelExpr, _applyChannelMap);
		Add(flowLayoutPanel4, "Slot范围=地址;映射", _daqProfiles);
		groupBox3.Controls.Add(flowLayoutPanel4);
		tableLayoutPanel2.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel2.Controls.Add(groupBox2, 0, 1);
		tableLayoutPanel2.Controls.Add(groupBox3, 0, 2);
		GroupBox groupBox4 = Card("温度采集/切换单元 Temp. Acquisition/Switch Unit");
		TableLayoutPanel tableLayoutPanel4 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			ColumnCount = 4,
			RowCount = 8,
			Padding = new Padding(16),
			AutoSize = true
		};
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		for (int i = 0; i < 16; i++)
		{
			int row = i / 2;
			int num = i % 2 * 2;
			tableLayoutPanel4.Controls.Add(new Label
			{
				Text = "卡" + (i + 1),
				AutoSize = true,
				Padding = new Padding(0, 8, 0, 0)
			}, num, row);
			tableLayoutPanel4.Controls.Add(_daqCardChannels[i], num + 1, row);
		}
		groupBox4.Controls.Add(tableLayoutPanel4);
		tableLayoutPanel.Controls.Add(tableLayoutPanel2, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox4, 1, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildDeviceListTab()
	{
		TabPage tabPage = new TabPage("设备")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
		GroupBox groupBox = Card("设备清单");
		groupBox.Controls.Add(_deviceGrid);
		GroupBox groupBox2 = Card("参数模板（选中行后填写）");
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(14),
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false
		};
		Add(flowLayoutPanel, "稳定时长(s)", _stableSec, "采集间隔/读数延时(s)", _settleSec, "超时(ms)", _timeout, "稳定误差(kPa)", _stableTolKpa, _preserveTempCoe, _writeBoard, _verifyAfterWrite);
		groupBox2.Controls.Add(flowLayoutPanel);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildInstrumentCommandTab()
	{
		TabPage tabPage = new TabPage("指令")
		{
			BackColor = Color.FromArgb(245, 248, 250),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12),
			BackColor = Color.White
		};
		Add(flowLayoutPanel, "设备型号", _commandModel, _loadCommandModel, _importIniCommand);
		GroupBox groupBox = Card("设备指令模板 Command.ini（9999或{0}会替换为压力值/通道号）");
		groupBox.Controls.Add(_commandGrid);
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildIndustrialRunTab()
	{
		TabPage tabPage = new TabPage("F40标定")
		{
			BackColor = IndustrialWorkspace,
			Padding = Padding.Empty
		};
		TableLayoutPanel workspace = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			BackColor = IndustrialWorkspace,
			Padding = Padding.Empty
		};
		workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268f));
		workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220f));
		Control context = BuildCalibrationContextPane();
		context.Margin = new Padding(0, 0, 8, 0);
		Control monitor = BuildCalibrationMonitorPane();
		monitor.Margin = Padding.Empty;
		Control rail = BuildCalibrationOperatorRail();
		rail.Margin = new Padding(8, 0, 0, 0);
		workspace.Controls.Add(context, 0, 0);
		workspace.Controls.Add(monitor, 1, 0);
		workspace.Controls.Add(rail, 2, 0);
		tabPage.Controls.Add(workspace);
		UpdateCalibrationOverview("待机，等待启动标定任务");
		return tabPage;
	}

	private Control BuildCalibrationContextPane()
	{
		Panel pane = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialSurface,
			BorderStyle = BorderStyle.FixedSingle,
			Padding = new Padding(10)
		};
		TableLayoutPanel layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 7,
			BackColor = IndustrialSurface,
			Padding = Padding.Empty
		};
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 154f));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80f));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		layout.Controls.Add(SectionTitle("生产批次 / 配方"), 0, 0);
		TableLayoutPanel model = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2,
			Margin = Padding.Empty
		};
		model.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		model.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82f));
		model.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		model.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		model.Controls.Add(FieldCaption("产品型号"), 0, 0);
		_calSensorModel.Dock = DockStyle.Fill;
		_calSensorModel.Margin = new Padding(0, 2, 6, 3);
		_applyCalModel.Dock = DockStyle.Fill;
		_applyCalModel.Margin = new Padding(0, 2, 0, 3);
		_applyCalModel.Text = "应用方案";
		model.Controls.Add(_calSensorModel, 0, 1);
		model.Controls.Add(_applyCalModel, 1, 1);
		layout.Controls.Add(model, 0, 1);
		TableLayoutPanel pressure = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 5,
			Margin = new Padding(0, 4, 0, 0),
			BackColor = IndustrialSurfaceAlt,
			Padding = new Padding(6, 4, 6, 4)
		};
		pressure.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
		pressure.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		pressure.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
		pressure.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		pressure.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		pressure.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		pressure.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		pressure.Controls.Add(FieldCaption("压力点"), 0, 0);
		pressure.SetColumnSpan(pressure.Controls[0], 2);
		_pressureUnit.Dock = DockStyle.Fill;
		_pressureUnit.Margin = new Padding(2, 0, 0, 1);
		pressure.Controls.Add(_pressureUnit, 2, 0);
		AddPressureRow(pressure, 1, "低点 P0", _p0);
		AddPressureRow(pressure, 2, "中点 Pmid", _pmid);
		AddPressureRow(pressure, 3, "满点 Pfull", _pfull);
		layout.Controls.Add(pressure, 0, 2);
		TableLayoutPanel source = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 3,
			Margin = new Padding(0, 8, 0, 0)
		};
		source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		source.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		source.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
		source.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		source.Controls.Add(FieldCaption("原始补偿 CSV"), 0, 0);
		source.SetColumnSpan(source.Controls[0], 2);
		_csvPath.Dock = DockStyle.Fill;
		_csvPath.Margin = new Padding(0, 2, 0, 2);
		source.Controls.Add(_csvPath, 0, 1);
		source.SetColumnSpan(_csvPath, 2);
		_browse.Dock = DockStyle.Fill;
		_browse.Margin = new Padding(0, 2, 4, 0);
		_browse.Text = "选择文件";
		_loadCsv.Dock = DockStyle.Fill;
		_loadCsv.Margin = new Padding(0, 2, 0, 0);
		_loadCsv.Text = "加载数据";
		source.Controls.Add(_browse, 0, 2);
		source.Controls.Add(_loadCsv, 1, 2);
		layout.Controls.Add(source, 0, 3);
		TableLayoutPanel selection = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 6,
			Margin = new Padding(0, 8, 0, 0),
			BackColor = IndustrialSurfaceAlt,
			Padding = new Padding(6, 4, 6, 4)
		};
		selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
		selection.Controls.Add(FieldCaption("工位选择"), 0, 0);
		selection.SetColumnSpan(selection.Controls[0], 2);
		_fillChannelSequenceRun.Dock = DockStyle.Fill;
		_fillChannelSequenceRun.Margin = new Padding(2);
		selection.Controls.Add(_fillChannelSequenceRun, 0, 1);
		selection.SetColumnSpan(_fillChannelSequenceRun, 2);
		Button[] selectButtons = new Button[6] { _selectValid, _selectDaq60, _selectStableF40Slots, _copyRawDataMap, _selectAll, _selectNone };
		for (int i = 0; i < selectButtons.Length; i++)
		{
			selectButtons[i].Dock = DockStyle.Fill;
			selectButtons[i].Margin = new Padding(2);
			selection.Controls.Add(selectButtons[i], i % 2, i / 2 + 2);
		}
		selection.Controls.Add(new Label
		{
			Text = "表格首列可单独勾选/排除工位",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = IndustrialMuted,
			Font = new Font("Microsoft YaHei UI", 7.8f)
		}, 0, 5);
		selection.SetColumnSpan(selection.Controls[selection.Controls.Count - 1], 2);
		layout.Controls.Add(selection, 0, 4);
		_calRecipeSummaryLabel = new Label
		{
			Dock = DockStyle.Fill,
			Margin = new Padding(0, 8, 0, 0),
			Padding = new Padding(8, 6, 8, 4),
			BackColor = Color.FromArgb(229, 237, 240),
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 8.2f),
			TextAlign = ContentAlignment.MiddleLeft
		};
		layout.Controls.Add(_calRecipeSummaryLabel, 0, 5);
		_calInterlockLabel = new Label
		{
			Dock = DockStyle.Top,
			Height = 64,
			Margin = new Padding(0, 8, 0, 0),
			Padding = new Padding(8, 7, 8, 5),
			BackColor = Color.FromArgb(245, 238, 216),
			ForeColor = Color.FromArgb(97, 67, 10),
			Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold),
			TextAlign = ContentAlignment.TopLeft
		};
		layout.Controls.Add(_calInterlockLabel, 0, 6);
		pane.Controls.Add(layout);
		return pane;

		static Label SectionTitle(string text)
		{
			return new Label
			{
				Text = text,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = IndustrialText,
				Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
				BorderStyle = BorderStyle.None
			};
		}

		static Label FieldCaption(string text)
		{
			return new Label
			{
				Text = text,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = IndustrialMuted,
				Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold)
			};
		}

		static void AddPressureRow(TableLayoutPanel parent, int row, string name, NumericUpDown value)
		{
			parent.Controls.Add(new Label
			{
				Text = name,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = IndustrialText,
				Font = new Font("Microsoft YaHei UI", 8f)
			}, 0, row);
			value.Dock = DockStyle.Fill;
			value.Margin = new Padding(2, 1, 2, 1);
			parent.Controls.Add(value, 1, row);
			parent.SetColumnSpan(value, 2);
		}
	}

	private Control BuildCalibrationMonitorPane()
	{
		TableLayoutPanel monitor = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 5,
			BackColor = IndustrialWorkspace,
			Padding = Padding.Empty
		};
		monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
		monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
		monitor.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
		monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
		TableLayoutPanel stage = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			BackColor = IndustrialSurface,
			BorderStyle = BorderStyle.FixedSingle,
			Padding = new Padding(8),
			Margin = Padding.Empty
		};
		stage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126f));
		stage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		stage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
		_calRunStateLabel = new Label
		{
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
			BackColor = Color.FromArgb(221, 229, 233),
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
			BorderStyle = BorderStyle.FixedSingle
		};
		_calStageLabel = new Label
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12, 0, 8, 0),
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
		_calProgressLabel = new Label
		{
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
			BackColor = IndustrialSurfaceAlt,
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
			BorderStyle = BorderStyle.FixedSingle
		};
		stage.Controls.Add(_calRunStateLabel, 0, 0);
		stage.Controls.Add(_calStageLabel, 1, 0);
		stage.Controls.Add(_calProgressLabel, 2, 0);
		monitor.Controls.Add(stage, 0, 0);
		monitor.Controls.Add(MonitorCaption("工位实时数据", "状态、测量值与写入结果按工位持续刷新"), 0, 1);
		_grid.Dock = DockStyle.Fill;
		_grid.Margin = Padding.Empty;
		_grid.RowTemplate.Height = 26;
		_grid.ColumnHeadersHeight = 30;
		_grid.BackgroundColor = Color.White;
		_grid.BorderStyle = BorderStyle.FixedSingle;
		monitor.Controls.Add(_grid, 0, 2);
		monitor.Controls.Add(MonitorCaption("事件与报警", "异常工位不会阻塞其他工位"), 0, 3);
		_log.Dock = DockStyle.Fill;
		_log.Margin = Padding.Empty;
		_log.BackColor = IndustrialConsole;
		_log.ForeColor = IndustrialConsoleText;
		_log.BorderStyle = BorderStyle.FixedSingle;
		_log.ReadOnly = true;
		_log.Font = new Font("Consolas", 9f);
		monitor.Controls.Add(_log, 0, 4);
		return monitor;

		static Control MonitorCaption(string title, string detail)
		{
			TableLayoutPanel bar = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				BackColor = Color.FromArgb(213, 222, 227),
				Padding = new Padding(8, 0, 8, 0),
				Margin = Padding.Empty
			};
			bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126f));
			bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			bar.Controls.Add(new Label
			{
				Text = title,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = IndustrialText,
				Font = new Font("Microsoft YaHei UI", 8.7f, FontStyle.Bold)
			}, 0, 0);
			bar.Controls.Add(new Label
			{
				Text = detail,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.MiddleRight,
				ForeColor = IndustrialMuted,
				Font = new Font("Microsoft YaHei UI", 7.8f)
			}, 1, 0);
			return bar;
		}
	}

	private Control BuildCalibrationOperatorRail()
	{
		Panel pane = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = IndustrialSurface,
			BorderStyle = BorderStyle.FixedSingle,
			Padding = new Padding(10)
		};
		TableLayoutPanel rail = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 14,
			BackColor = IndustrialSurface,
			Padding = Padding.Empty
		};
		float[] heights = new float[13] { 34f, 50f, 44f, 34f, 36f, 42f, 28f, 38f, 38f, 38f, 28f, 82f, 36f };
		foreach (float height in heights)
		{
			rail.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
		}
		rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		rail.Controls.Add(new Label
		{
			Text = "运行控制",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
		}, 0, 0);
		_start.Text = "启动逐工位标定";
		_stop.Text = "停止 / 泄压";
		StyleHmiButton(_start, IndustrialSuccess, Color.White, 44);
		StyleHmiButton(_stop, IndustrialDanger, Color.White, 38);
		rail.Controls.Add(_start, 0, 1);
		rail.Controls.Add(_stop, 0, 2);
		_batchPressureMode.Text = "批量稳压加速";
		_batchPressureMode.Dock = DockStyle.Fill;
		_batchPressureMode.Margin = new Padding(4, 2, 4, 2);
		rail.Controls.Add(_batchPressureMode, 0, 3);
		TableLayoutPanel single = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			Margin = Padding.Empty
		};
		single.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		single.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72f));
		single.Controls.Add(new Label
		{
			Text = "指定工位 Slot",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = IndustrialMuted,
			Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold)
		}, 0, 0);
		_singleCalSlot.Dock = DockStyle.Fill;
		_singleCalSlot.Margin = new Padding(2, 4, 0, 4);
		single.Controls.Add(_singleCalSlot, 1, 0);
		rail.Controls.Add(single, 0, 4);
		_startSingleCal.Text = "标定指定工位";
		StyleHmiButton(_startSingleCal, IndustrialAccent, Color.White, 36);
		rail.Controls.Add(_startSingleCal, 0, 5);
		rail.Controls.Add(RailCaption("数据与写入"), 0, 6);
		_calcSelected.Text = "只计算选中系数";
		_writeSelected.Text = "写入选中工位";
		_writePreCalConfig.Text = "写标定前配置";
		StyleHmiButton(_calcSelected, IndustrialSurfaceAlt, IndustrialText, 32);
		StyleHmiButton(_writeSelected, IndustrialAccent, Color.White, 32);
		StyleHmiButton(_writePreCalConfig, IndustrialSurfaceAlt, IndustrialText, 32);
		rail.Controls.Add(_calcSelected, 0, 7);
		rail.Controls.Add(_writeSelected, 0, 8);
		rail.Controls.Add(_writePreCalConfig, 0, 9);
		rail.Controls.Add(RailCaption("参数与维护"), 0, 10);
		TableLayoutPanel maintenance = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2,
			Margin = Padding.Empty
		};
		maintenance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		maintenance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		maintenance.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
		maintenance.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
		maintenance.Controls.Add(RailButton("标定参数", delegate { ShowCalibrationSettingsDialog(); }), 0, 0);
		maintenance.Controls.Add(RailButton("产品配置", delegate { ShowPreCalibrationSettingsDialog(); }), 1, 0);
		maintenance.Controls.Add(RailButton("设备 / DAQ", delegate { ShowDeviceDaqSettingsDialog(); }), 0, 1);
		maintenance.Controls.Add(RailButton("手动调试", delegate { ShowManualDebugDialog(); }), 1, 1);
		rail.Controls.Add(maintenance, 0, 11);
		Button logButton = RailButton("打开日志目录", delegate { OpenLogDirectory(); });
		logButton.Margin = new Padding(2, 3, 2, 1);
		rail.Controls.Add(logButton, 0, 12);
		rail.Controls.Add(new Label
		{
			Text = "停止任务会取消当前流程，并尝试关闭压力输出与执行泄压。",
			Dock = DockStyle.Top,
			Height = 58,
			Padding = new Padding(4, 10, 4, 0),
			ForeColor = IndustrialMuted,
			Font = new Font("Microsoft YaHei UI", 7.8f),
			TextAlign = ContentAlignment.TopLeft
		}, 0, 13);
		pane.Controls.Add(rail);
		return pane;

		static Label RailCaption(string text)
		{
			return new Label
			{
				Text = text,
				Dock = DockStyle.Fill,
				TextAlign = ContentAlignment.BottomLeft,
				ForeColor = IndustrialMuted,
				Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold),
				Padding = new Padding(2, 0, 0, 3)
			};
		}

		static Button RailButton(string text, EventHandler click)
		{
			Button button = new Button
			{
				Text = text,
				Dock = DockStyle.Fill,
				Margin = new Padding(2),
				BackColor = IndustrialSurfaceAlt,
				ForeColor = IndustrialText,
				FlatStyle = FlatStyle.Flat,
				Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold)
			};
			button.FlatAppearance.BorderColor = IndustrialHeaderBorder;
			button.Click += click;
			return button;
		}
	}

	private static void StyleHmiButton(Button button, Color backColor, Color foreColor, int height)
	{
		button.Dock = DockStyle.Fill;
		button.Height = height;
		button.Margin = new Padding(2, 3, 2, 3);
		button.FlatStyle = FlatStyle.Flat;
		button.FlatAppearance.BorderColor = IndustrialHeaderBorder;
		button.BackColor = backColor;
		button.ForeColor = foreColor;
		button.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
	}

	private TabPage BuildRunTab()
	{
		TabPage tabPage = new TabPage("标定运行")
		{
			BackColor = IndustrialWorkspace,
			Padding = new Padding(8)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			BackColor = tabPage.BackColor
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 126f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 190f));
		Control control = BuildRunCommandBar();
		control.Margin = new Padding(0, 0, 0, 8);
		_grid.Dock = DockStyle.Fill;
		_grid.RowTemplate.Height = 26;
		_grid.ColumnHeadersHeight = 30;
		_grid.GridColor = Color.FromArgb(180, 190, 200);
		_grid.BackgroundColor = Color.White;
		_grid.BorderStyle = BorderStyle.FixedSingle;
		_grid.EnableHeadersVisualStyles = false;
		_grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.ControlLight;
		_grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
		_grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold);
		_grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold);
		_grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 248, 248);
		GroupBox groupBox = LegacyGroup("运行日志");
		groupBox.Margin = new Padding(0, 8, 0, 0);
		_log.Dock = DockStyle.Fill;
		_log.BackColor = IndustrialConsole;
		_log.ForeColor = IndustrialConsoleText;
		_log.BorderStyle = BorderStyle.FixedSingle;
		_log.ReadOnly = true;
		_log.Font = new Font("Consolas", 9f);
		groupBox.Controls.Add(_log);
		GroupBox groupBox2 = LegacyGroup("工位标定明细");
		groupBox2.Margin = new Padding(0);
		groupBox2.Controls.Add(_grid);
		tableLayoutPanel.Controls.Add(control, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 0, 1);
		tableLayoutPanel.Controls.Add(groupBox, 0, 2);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private Control BuildRunCommandBar()
	{
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 1,
			BackColor = IndustrialWorkspace
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
		GroupBox production = LegacyGroup("生产运行");
		GroupBox data = LegacyGroup("数据与写入");
		GroupBox setup = LegacyGroup("参数与维护");
		production.Margin = new Padding(0, 0, 8, 0);
		data.Margin = new Padding(0, 0, 8, 0);
		setup.Margin = Padding.Empty;
		FlowLayoutPanel productionFlow = CommandFlow();
		FlowLayoutPanel dataFlow = CommandFlow();
		FlowLayoutPanel setupFlow = CommandFlow();
		_start.Text = "开始逐工位标定";
		_startSingleCal.Text = "标定指定工位";
		_stop.Text = "停止";
		StyleCommandButton(_start, IndustrialSuccess, Color.White, 112);
		StyleCommandButton(_startSingleCal, IndustrialAccent, Color.White, 112);
		StyleCommandButton(_stop, IndustrialDanger, Color.White, 72);
		_batchPressureMode.AutoSize = true;
		_batchPressureMode.Margin = new Padding(10, 8, 6, 0);
		_singleCalSlot.Width = 58;
		_singleCalSlot.Margin = new Padding(4, 5, 0, 0);
		productionFlow.Controls.Add(_start);
		productionFlow.Controls.Add(_stop);
		productionFlow.Controls.Add(_batchPressureMode);
		productionFlow.Controls.Add(new Label
		{
			Text = "工位",
			AutoSize = true,
			Margin = new Padding(10, 9, 0, 0)
		});
		productionFlow.Controls.Add(_singleCalSlot);
		productionFlow.Controls.Add(_startSingleCal);
		Button browse = CommandButton("选择CSV", 82, delegate
		{
			_browse.PerformClick();
		});
		Button load = CommandButton("加载CSV", 82, delegate
		{
			_loadCsv.PerformClick();
		});
		StyleCommandButton(_calcSelected, IndustrialSurfaceAlt, IndustrialText, 90);
		StyleCommandButton(_writeSelected, IndustrialAccent, Color.White, 90);
		StyleCommandButton(_writePreCalConfig, IndustrialSurfaceAlt, IndustrialText, 112);
		dataFlow.Controls.Add(browse);
		dataFlow.Controls.Add(load);
		dataFlow.Controls.Add(_calcSelected);
		dataFlow.Controls.Add(_writeSelected);
		dataFlow.Controls.Add(_writePreCalConfig);
		setupFlow.Controls.Add(CommandButton("标定参数", 88, delegate
		{
			ShowCalibrationSettingsDialog();
		}));
		setupFlow.Controls.Add(CommandButton("产品配置", 88, delegate
		{
			ShowPreCalibrationSettingsDialog();
		}));
		setupFlow.Controls.Add(CommandButton("设备 / DAQ", 94, delegate
		{
			ShowDeviceDaqSettingsDialog();
		}));
		setupFlow.Controls.Add(CommandButton("指令模板", 88, delegate
		{
			ShowCommandSettingsDialog();
		}));
		setupFlow.Controls.Add(CommandButton("手动调试", 88, delegate
		{
			ShowManualDebugDialog();
		}));
		setupFlow.Controls.Add(CommandButton("日志目录", 88, delegate
		{
			OpenLogDirectory();
		}));
		production.Controls.Add(productionFlow);
		data.Controls.Add(dataFlow);
		setup.Controls.Add(setupFlow);
		tableLayoutPanel.Controls.Add(production, 0, 0);
		tableLayoutPanel.Controls.Add(data, 1, 0);
		tableLayoutPanel.Controls.Add(setup, 2, 0);
		return tableLayoutPanel;

		static FlowLayoutPanel CommandFlow()
		{
			return new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = true,
				Padding = new Padding(8, 8, 4, 4),
				BackColor = IndustrialSurface
			};
		}

		static Button CommandButton(string text, int width, EventHandler click)
		{
			Button button = new Button();
			StyleCommandButton(button, IndustrialSurfaceAlt, IndustrialText, width);
			button.Text = text;
			button.Click += click;
			return button;
		}

		static void StyleCommandButton(Button button, Color backColor, Color foreColor, int width)
		{
			button.Width = width;
			button.Height = 34;
			button.Margin = new Padding(4);
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = IndustrialHeaderBorder;
			button.BackColor = backColor;
			button.ForeColor = foreColor;
			button.Font = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold);
		}
	}

	private Control BuildRunControlRail()
	{
		GroupBox groupBox = LegacyGroup("运行操作");
		groupBox.Padding = new Padding(8, 16, 8, 8);
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 19,
			BackColor = SystemColors.Control
		};
		for (int i = 0; i < 19; i++)
		{
			tableLayoutPanel.RowStyles.Add(new RowStyle((i != 18) ? SizeType.Absolute : SizeType.Percent, (i == 18) ? 100 : 36));
		}
		Button[] array = new Button[6] { _start, _startSingleCal, _stop, _calcSelected, _writeSelected, _writePreCalConfig };
		foreach (Button b in array)
		{
			StyleRunButton(b);
		}
		_start.BackColor = IndustrialSuccess;
		_start.ForeColor = Color.White;
		_stop.BackColor = IndustrialDanger;
		_stop.ForeColor = Color.White;
		_batchPressureMode.Dock = DockStyle.Fill;
		_batchPressureMode.Margin = new Padding(8, 3, 4, 3);
		_batchPressureMode.TextAlign = ContentAlignment.MiddleLeft;
		_batchPressureMode.Font = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold);
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			Margin = Padding.Empty
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel2.Controls.Add(new Label
		{
			Text = "单独工位",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Bold)
		}, 0, 0);
		_singleCalSlot.Dock = DockStyle.Fill;
		_singleCalSlot.Margin = new Padding(4, 5, 4, 5);
		tableLayoutPanel2.Controls.Add(_singleCalSlot, 1, 0);
		int num = 0;
		tableLayoutPanel.Controls.Add(_start, 0, num++);
		tableLayoutPanel.Controls.Add(_batchPressureMode, 0, num++);
		tableLayoutPanel.Controls.Add(tableLayoutPanel2, 0, num++);
		tableLayoutPanel.Controls.Add(_startSingleCal, 0, num++);
		tableLayoutPanel.Controls.Add(_stop, 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("选择原始CSV", delegate
		{
			_browse.PerformClick();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("加载CSV", delegate
		{
			_loadCsv.PerformClick();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(_calcSelected, 0, num++);
		tableLayoutPanel.Controls.Add(_writeSelected, 0, num++);
		tableLayoutPanel.Controls.Add(_writePreCalConfig, 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("标定配置", delegate
		{
			ShowCalibrationSettingsDialog();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("写配置设置", delegate
		{
			ShowPreCalibrationSettingsDialog();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("设备/DAQ", delegate
		{
			ShowDeviceDaqSettingsDialog();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("指令配置", delegate
		{
			ShowCommandSettingsDialog();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("手动调试", delegate
		{
			ShowManualDebugDialog();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(LocalButton("打开日志", delegate
		{
			OpenLogDirectory();
		}), 0, num++);
		tableLayoutPanel.Controls.Add(new Label
		{
			Dock = DockStyle.Fill,
			Text = "默认按原程序逐工位闭环；批量稳压为融合版加速调度，压力节奏不同。",
			TextAlign = ContentAlignment.TopLeft,
			ForeColor = Color.FromArgb(71, 85, 105),
			Padding = new Padding(6, 8, 4, 0),
			Font = new Font("Microsoft YaHei UI", 8f)
		}, 0, 18);
		groupBox.Controls.Add(tableLayoutPanel);
		return groupBox;
		static Button LocalButton(string text, EventHandler click)
		{
			Button button = new Button
			{
				Text = text,
				Dock = DockStyle.Fill,
				Height = 30,
				Margin = new Padding(4, 3, 4, 3),
				Font = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold),
				UseVisualStyleBackColor = true
			};
			button.Click += click;
			return button;
		}
		static void StyleRunButton(Button button)
		{
			button.Dock = DockStyle.Fill;
			button.Height = 30;
			button.Margin = new Padding(4, 3, 4, 3);
			button.Font = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold);
		}
	}

	private GroupBox BuildDataSourceCard()
	{
		GroupBox groupBox = Card("数据源 / 工位选择");
		groupBox.Padding = new Padding(10, 16, 10, 8);
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 4
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4
		};
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));
		tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel2.Controls.Add(new Label
		{
			Text = "原始CSV",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, 0, 0);
		_csvPath.Dock = DockStyle.Fill;
		_csvPath.Margin = new Padding(8, 6, 8, 4);
		tableLayoutPanel2.Controls.Add(_csvPath, 1, 0);
		_browse.Dock = DockStyle.Fill;
		_loadCsv.Dock = DockStyle.Fill;
		tableLayoutPanel2.Controls.Add(_browse, 2, 0);
		tableLayoutPanel2.Controls.Add(_loadCsv, 3, 0);
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4
		};
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel3.Controls.Add(new Label
		{
			Text = "标定型号",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, 0, 0);
		_calSensorModel.Dock = DockStyle.Fill;
		_calSensorModel.Margin = new Padding(8, 4, 8, 4);
		tableLayoutPanel3.Controls.Add(_calSensorModel, 1, 0);
		tableLayoutPanel3.Controls.Add(new Label
		{
			Text = "型号应用",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, 2, 0);
		_applyCalModel.Dock = DockStyle.Left;
		_applyCalModel.Width = 110;
		_applyCalModel.Height = 28;
		_applyCalModel.Margin = new Padding(8, 4, 0, 4);
		tableLayoutPanel3.Controls.Add(_applyCalModel, 3, 0);
		TableLayoutPanel tableLayoutPanel4 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 9
		};
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66f));
		tableLayoutPanel4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel4.Controls.Add(new Label
		{
			Text = "压力点",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, 0, 0);
		tableLayoutPanel4.Controls.Add(new Label
		{
			Text = "P0",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter
		}, 1, 0);
		tableLayoutPanel4.Controls.Add(new Label
		{
			Text = "Pmid",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter
		}, 3, 0);
		tableLayoutPanel4.Controls.Add(new Label
		{
			Text = "Pfull",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter
		}, 5, 0);
		NumericUpDown[] array = new NumericUpDown[3] { _p0, _pmid, _pfull };
		foreach (NumericUpDown numericUpDown in array)
		{
			numericUpDown.Dock = DockStyle.Fill;
			numericUpDown.Margin = new Padding(4);
		}
		tableLayoutPanel4.Controls.Add(_p0, 2, 0);
		tableLayoutPanel4.Controls.Add(_pmid, 4, 0);
		tableLayoutPanel4.Controls.Add(_pfull, 6, 0);
		_pressureUnit.Dock = DockStyle.Left;
		_pressureUnit.Width = 60;
		_pressureUnit.Margin = new Padding(4);
		tableLayoutPanel4.Controls.Add(_pressureUnit, 7, 0);
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(78, 2, 0, 0),
			WrapContents = false
		};
		Button[] array2 = new Button[4] { _selectValid, _selectDaq60, _selectAll, _selectNone };
		foreach (Button button in array2)
		{
			button.Height = 30;
			button.Width = Math.Max(button.Width, 88);
			button.Margin = new Padding(0, 0, 10, 0);
			flowLayoutPanel.Controls.Add(button);
		}
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "单独工位",
			AutoSize = true,
			Padding = new Padding(6, 7, 0, 0),
			ForeColor = Color.FromArgb(51, 65, 85)
		});
		_singleCalSlot.Height = 28;
		_singleCalSlot.Width = 64;
		_singleCalSlot.Margin = new Padding(6, 0, 8, 0);
		flowLayoutPanel.Controls.Add(_singleCalSlot);
		tableLayoutPanel.Controls.Add(tableLayoutPanel2, 0, 0);
		tableLayoutPanel.Controls.Add(tableLayoutPanel3, 0, 1);
		tableLayoutPanel.Controls.Add(tableLayoutPanel4, 0, 2);
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 3);
		groupBox.Controls.Add(tableLayoutPanel);
		return groupBox;
	}

	private GroupBox BuildPressureCard()
	{
		GroupBox groupBox = Card("当前标定策略");
		TextBox value = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			BackColor = Color.White,
			ForeColor = Color.FromArgb(51, 65, 85),
			Font = new Font("Microsoft YaHei UI", 9.5f),
			Text = "① 压力点、稳压时间、DAQ地址、写入策略：在【设备/DAQ配置】页设置并保存。\r\n② 本页只负责：加载CSV → 选择工位 → 计算/写入/自动标定。\r\n③ 自动标定：按原版流程先连续拉零点，再连续拉满点；只有不合格工位才进入下一轮。"
		};
		groupBox.Controls.Add(value);
		return groupBox;
	}

	private GroupBox BuildActionCard()
	{
		GroupBox groupBox = Card("运行控制 / 配置");
		groupBox.Padding = new Padding(8, 10, 8, 6);
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2,
			BackColor = Color.White
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 6,
			RowCount = 2,
			BackColor = Color.White,
			Margin = Padding.Empty
		};
		for (int i = 0; i < 6; i++)
		{
			tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));
		}
		for (int j = 0; j < 2; j++)
		{
			tableLayoutPanel2.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
		}
		Button button = new Button
		{
			Text = "标定配置",
			Height = 26
		};
		Button button2 = new Button
		{
			Text = "写配置设置",
			Height = 26
		};
		Button button3 = new Button
		{
			Text = "设备/DAQ",
			Height = 26
		};
		Button button4 = new Button
		{
			Text = "指令配置",
			Height = 26
		};
		Button button5 = new Button
		{
			Text = "打开日志",
			Height = 26
		};
		Control[] array = new Control[12]
		{
			_calcSelected, _writeSelected, _start, _startSingleCal, _stop, _batchPressureMode, button, button2, button3, button4,
			_writePreCalConfig, button5
		};
		foreach (Button item in array.OfType<Button>())
		{
			item.Dock = DockStyle.Fill;
			item.Margin = new Padding(3);
			item.MinimumSize = new Size(72, 26);
			item.AutoEllipsis = false;
			item.Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);
		}
		_batchPressureMode.Dock = DockStyle.Fill;
		_batchPressureMode.Margin = new Padding(8, 3, 3, 3);
		_batchPressureMode.TextAlign = ContentAlignment.MiddleCenter;
		_batchPressureMode.Font = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold);
		_batchPressureMode.BackColor = Color.FromArgb(240, 253, 244);
		for (int k = 0; k < array.Length; k++)
		{
			tableLayoutPanel2.Controls.Add(array[k], k % 6, k / 6);
		}
		_calcSelected.BackColor = Color.FromArgb(226, 232, 240);
		_writeSelected.BackColor = Color.FromArgb(219, 234, 254);
		_start.BackColor = Color.FromArgb(22, 163, 74);
		_start.ForeColor = Color.White;
		_stop.BackColor = Color.FromArgb(254, 226, 226);
		_stop.ForeColor = Color.FromArgb(153, 27, 27);
		button.BackColor = Color.FromArgb(226, 232, 240);
		button2.BackColor = Color.FromArgb(226, 232, 240);
		button3.BackColor = Color.FromArgb(226, 232, 240);
		button4.BackColor = Color.FromArgb(226, 232, 240);
		button5.BackColor = Color.FromArgb(226, 232, 240);
		button.Click += delegate
		{
			ShowCalibrationSettingsDialog();
		};
		button2.Click += delegate
		{
			ShowPreCalibrationSettingsDialog();
		};
		button3.Click += delegate
		{
			ShowDeviceDaqSettingsDialog();
		};
		button4.Click += delegate
		{
			ShowCommandSettingsDialog();
		};
		button5.Click += delegate
		{
			OpenLogDirectory();
		};
		tableLayoutPanel.Controls.Add(tableLayoutPanel2, 0, 0);
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = "流程：按轮次批量加压 → 批量采集 → 写系数；不合格工位自动继续下一轮。",
			Dock = DockStyle.Fill,
			ForeColor = Color.FromArgb(71, 85, 105),
			Font = new Font("Microsoft YaHei UI", 7.8f),
			TextAlign = ContentAlignment.MiddleLeft
		}, 0, 1);
		groupBox.Controls.Add(tableLayoutPanel);
		return groupBox;
	}

	private void ShowCalibrationSettingsDialog()
	{
		using Form form = new Form
		{
			Text = "标定配置",
			StartPosition = FormStartPosition.CenterParent,
			Width = 560,
			Height = 470,
			MinimizeBox = false,
			MaximizeBox = false,
			FormBorderStyle = FormBorderStyle.FixedDialog
		};
		ComboBox model = ComboWith(_calSensorModel.Text, _calSensorModel.Items.Cast<object>().Select((object x) => Convert.ToString(x) ?? "").Where((string x) => !string.IsNullOrWhiteSpace(x)).ToArray());
		Button applyPlan = new Button
		{
			Text = "套用原INI",
			Width = 100
		};
		NumericUpDown p0 = CloneNumeric(_p0);
		NumericUpDown pmid = CloneNumeric(_pmid);
		NumericUpDown pfull = CloneNumeric(_pfull);
		ComboBox unit = ComboWith(_pressureUnit.Text, "psi", "kPa");
		unit.DropDownStyle = ComboBoxStyle.DropDownList;
		NumericUpDown outputMin = CloneNumeric(_calOutputMinV);
		NumericUpDown outputMax = CloneNumeric(_calOutputMaxV);
		NumericUpDown percentMin = CloneNumeric(_calPercentMin);
		NumericUpDown percentMax = CloneNumeric(_calPercentMax);
		NumericUpDown stableTol = CloneNumeric(_stableTolKpa);
		NumericUpDown stableSec = CloneNumeric(_stableSec);
		NumericUpDown settleSec = CloneNumeric(_settleSec);
		NumericUpDown outputTol = CloneNumeric(_calVoltageTolerance);
		NumericUpDown retryCount = CloneNumeric(_calMaxRetryCount);
		CheckBox linearity = new CheckBox
		{
			Text = _calLinearityEnabled.Text,
			Checked = _calLinearityEnabled.Checked,
			Width = 120
		};
		CheckBox preserve = new CheckBox
		{
			Text = _preserveTempCoe.Text,
			Checked = _preserveTempCoe.Checked,
			Width = 150
		};
		CheckBox write = new CheckBox
		{
			Text = _writeBoard.Text,
			Checked = _writeBoard.Checked,
			Width = 150
		};
		CheckBox verify = new CheckBox
		{
			Text = _verifyAfterWrite.Text,
			Checked = _verifyAfterWrite.Checked,
			Width = 120
		};
		CheckBox batch = new CheckBox
		{
			Text = "批量稳压加速(非原节奏)",
			Checked = _batchPressureMode.Checked,
			Width = 190
		};
		applyPlan.Click += delegate
		{
			try
			{
				CalibrationPlan plan = BuildCalibrationPlan(model.Text.Trim());
				p0.Value = ClampDecimal((decimal)plan.P0, p0.Minimum, p0.Maximum);
				pmid.Value = ClampDecimal((decimal)plan.Pmid, pmid.Minimum, pmid.Maximum);
				pfull.Value = ClampDecimal((decimal)plan.Pfull, pfull.Minimum, pfull.Maximum);
				unit.Text = NormalizePressureUnit(plan.PressureUnit);
				outputMin.Value = ClampDecimal((decimal)plan.OutputMinV, outputMin.Minimum, outputMin.Maximum);
				outputMax.Value = ClampDecimal((decimal)plan.OutputMaxV, outputMax.Minimum, outputMax.Maximum);
				percentMin.Value = ClampDecimal((decimal)plan.PercentMin, percentMin.Minimum, percentMin.Maximum);
				percentMax.Value = ClampDecimal((decimal)plan.PercentMax, percentMax.Minimum, percentMax.Maximum);
				outputTol.Value = ClampDecimal((decimal)Math.Max(0.0, plan.DacToleranceV), outputTol.Minimum, outputTol.Maximum);
				linearity.Checked = plan.LinearityEnabled;
				Log($"标定配置弹窗已套用原INI：{plan.Model}，来源={(File.Exists(plan.Source) ? Path.GetFileName(plan.Source) : plan.Source)}", important: true);
			}
			catch (Exception ex)
			{
				MessageBox.Show(form, "套用原INI失败：" + ex.Message, "标定配置", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		};
		TableLayoutPanel tableLayoutPanel = DialogGrid(10);
		AddDialogRow(tableLayoutPanel, 0, "型号方案", model, applyPlan);
		AddDialogRow(tableLayoutPanel, 1, "P0 / Pmid / Pfull", p0, pmid, pfull, unit);
		AddDialogRow(tableLayoutPanel, 2, "输出低/满V", outputMin, outputMax);
		AddDialogRow(tableLayoutPanel, 3, "目标低/满% / 容差V", percentMin, percentMax, outputTol, linearity);
		AddDialogRow(tableLayoutPanel, 4, "稳压容差/稳压s/延时s", stableTol, stableSec, settleSec);
		AddDialogRow(tableLayoutPanel, 5, "最大复标次数(0=不限)", retryCount);
		AddDialogRow(tableLayoutPanel, 6, "写入策略/模式", preserve, write, verify, batch);
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = "说明：默认关闭批量稳压，按原程序逐工位闭环执行：0点/满点首读、两点修正写系数、0点/满点复测、合格后中点线性。批量稳压只复用每槽算法，压力调度节奏不是原程序原样。",
			Dock = DockStyle.Fill,
			ForeColor = Color.FromArgb(71, 85, 105)
		}, 0, 7);
		tableLayoutPanel.SetColumnSpan(tableLayoutPanel.GetControlFromPosition(0, 7), 5);
		AddOkCancel(form, tableLayoutPanel, delegate
		{
			_calSensorModel.Text = model.Text.Trim();
			CopyNumeric(p0, _p0);
			CopyNumeric(pmid, _pmid);
			CopyNumeric(pfull, _pfull);
			_pressureUnit.Text = unit.Text;
			CopyNumeric(outputMin, _calOutputMinV);
			CopyNumeric(outputMax, _calOutputMaxV);
			CopyNumeric(percentMin, _calPercentMin);
			CopyNumeric(percentMax, _calPercentMax);
			CopyNumeric(stableTol, _stableTolKpa);
			CopyNumeric(stableSec, _stableSec);
			CopyNumeric(settleSec, _settleSec);
			CopyNumeric(outputTol, _calVoltageTolerance);
			CopyNumeric(retryCount, _calMaxRetryCount);
			_preserveTempCoe.Checked = true;
			_writeBoard.Checked = write.Checked;
			_verifyAfterWrite.Checked = verify.Checked;
			_calLinearityEnabled.Checked = linearity.Checked;
			_batchPressureMode.Checked = batch.Checked;
			ApplyCalibrationTargetsToRows(resetDesiredPercents: true);
			Log("标定配置已更新。", important: true);
		});
		form.ShowDialog(this);
	}

	private void ShowPreCalibrationSettingsDialog()
	{
		using Form form = new Form
		{
			Text = "标定前写配置",
			StartPosition = FormStartPosition.CenterParent,
			Width = 620,
			Height = 400,
			MinimizeBox = false,
			MaximizeBox = false,
			FormBorderStyle = FormBorderStyle.FixedDialog
		};
		CheckBox enable = new CheckBox
		{
			Text = _writeConfigBeforeCal.Text,
			Checked = _writeConfigBeforeCal.Checked,
			Width = 160
		};
		NumericUpDown board = CloneNumeric(_preCalBoardAddr);
		ComboBox group = ComboWith(_preCalConfigGroup.Text, "0304", "1415");
		group.DropDownStyle = ComboBoxStyle.DropDownList;
		NumericUpDown startSlot = CloneNumeric(_preCalStartSlot);
		NumericUpDown count = CloneNumeric(_preCalConfigCount);
		TextBox regA = new TextBox
		{
			Text = _preCalRegAHex.Text,
			Width = 90
		};
		TextBox regB = new TextBox
		{
			Text = _preCalRegBHex.Text,
			Width = 90
		};
		TextBox map = new TextBox
		{
			Text = _boardSlotMapCal.Text,
			Width = 330
		};
		CheckBox use47 = new CheckBox
		{
			Text = _useBoardChannel47.Text,
			Checked = _useBoardChannel47.Checked,
			Width = 120
		};
		TableLayoutPanel tableLayoutPanel = DialogGrid(8);
		AddDialogRow(tableLayoutPanel, 0, "启用", enable);
		AddDialogRow(tableLayoutPanel, 1, "板卡/寄存器/起始/数量", board, group, startSlot, count);
		AddDialogRow(tableLayoutPanel, 2, "写入A / 写入B", regA, regB);
		AddDialogRow(tableLayoutPanel, 3, "板卡范围", map, use47);
		tableLayoutPanel.Controls.Add(new Label
		{
			Text = "自动标定时每个工位单独写配置；某个工位写配置失败会标红并跳过，不再中断后续工位。",
			Dock = DockStyle.Fill,
			ForeColor = Color.FromArgb(71, 85, 105)
		}, 0, 4);
		tableLayoutPanel.SetColumnSpan(tableLayoutPanel.GetControlFromPosition(0, 4), 5);
		AddOkCancel(form, tableLayoutPanel, delegate
		{
			_writeConfigBeforeCal.Checked = enable.Checked;
			CopyNumeric(board, _preCalBoardAddr);
			_preCalConfigGroup.Text = group.Text;
			CopyNumeric(startSlot, _preCalStartSlot);
			CopyNumeric(count, _preCalConfigCount);
			_preCalRegAHex.Text = regA.Text.Trim();
			_preCalRegBHex.Text = regB.Text.Trim();
			_boardSlotMapCal.Text = map.Text.Trim();
			_boardSlotMap.Text = map.Text.Trim();
			_boardSlotMapDevice.Text = map.Text.Trim();
			_useBoardChannel47.Checked = use47.Checked;
			_useBoardChannel47Manual.Checked = use47.Checked;
			Log("标定前写配置参数已更新。", important: true);
		});
		form.ShowDialog(this);
	}

	private void ShowLegacyDialogSafely(string title, Action showDialog, Action<string> log)
	{
		try
		{
			log("打开" + title + "窗口。");
			showDialog();
		}
		catch (Exception ex)
		{
			string text = title + "窗口打开失败：" + ex.Message;
			log(text);
			MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private void ShowDeviceDaqSettingsDialog()
	{
		Form form = new Form
		{
			Text = "录入仪器配置",
			StartPosition = FormStartPosition.CenterParent,
			Width = 520,
			Height = 420,
			MinimizeBox = false,
			MaximizeBox = false,
			BackColor = SystemColors.Control
		};
		try
		{
			TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				RowCount = 2,
				Padding = new Padding(10),
				BackColor = SystemColors.Control
			};
			tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
			tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
			TabControl tabControl = new TabControl
			{
				Dock = DockStyle.Fill
			};
			tabControl.TabPages.Add(BuildLegacyBoardDevicePage());
			tabControl.TabPages.Add(BuildLegacySwitchDevicePage());
			tabControl.TabPages.Add(BuildLegacyOvenDevicePage());
			tabControl.TabPages.Add(BuildLegacyPressureDevicePage());
			tabControl.TabPages.Add(BuildLegacyDmmDevicePage());
			FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.RightToLeft,
				Padding = new Padding(0, 7, 0, 0)
			};
			Button button = new Button
			{
				Text = "返回",
				Width = 90,
				Height = 30,
				ForeColor = Color.Red
			};
			Button button2 = new Button
			{
				Text = "保存",
				Width = 90,
				Height = 30
			};
			button2.Click += delegate
			{
				SyncGpibComboFromAddress(_pressureAddr, _pressureGpibPort, _pressureGpibAddress);
				SyncGpibComboFromAddress(_dmmAddr, _dmmGpibPort, _dmmGpibAddress);
				SyncDaqGridFromText();
				ApplyChannelMap();
				UpdateDeviceStatusPanel();
				SaveAppConfig();
				Log("录入仪器配置已保存。", important: true);
			};
			button.Click += delegate
			{
				form.Close();
			};
			flowLayoutPanel.Controls.Add(button);
			flowLayoutPanel.Controls.Add(button2);
			tableLayoutPanel.Controls.Add(tabControl, 0, 0);
			tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 1);
			form.Controls.Add(tableLayoutPanel);
			form.ShowDialog(this);
		}
		finally
		{
			if (form != null)
			{
				((IDisposable)form).Dispose();
			}
		}
	}

	private void ShowCommandSettingsDialog()
	{
		Form form = new Form
		{
			Text = "录入仪器指令",
			StartPosition = FormStartPosition.CenterParent,
			Width = 550,
			Height = 590,
			MinimizeBox = false,
			MaximizeBox = false,
			BackColor = SystemColors.Control
		};
		try
		{
			List<Action> saveActions = new List<Action>();
			TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				RowCount = 2,
				Padding = new Padding(10),
				BackColor = SystemColors.Control
			};
			tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
			tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
			TabControl tabControl = new TabControl
			{
				Dock = DockStyle.Fill
			};
			tabControl.TabPages.Add(BuildLegacyCommandPage("烘箱", _compOvenModel.Text, IsTcpOvenModel(_compOvenModel.Text) ? "TCP/IP" : "RS232", new string[6] { "Open", "Set", "Stop", "Read", "Mode", "Type" }, saveActions));
			tabControl.TabPages.Add(BuildLegacyCommandPage("压力控制器", _pressureModel.Text, "GPIB", new string[12]
			{
				"Open", "Machine Type", "UpperLimt", "ZeroCheck", "ReadPressure", "SetMeasure", "SetPressure", "Vent", "SetAbs", "SelfTest",
				"ReadStatus", "SetGaug"
			}, saveActions));
			tabControl.TabPages.Add(BuildLegacyCommandPage("切换单元", _dmmModel.Text, "GPIB", new string[2] { "Open", "Close" }, saveActions));
			FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.RightToLeft,
				Padding = new Padding(0, 10, 100, 0)
			};
			Button button = new Button
			{
				Text = "返回",
				Width = 90,
				Height = 30,
				ForeColor = Color.Red
			};
			Button button2 = new Button
			{
				Text = "保存",
				Width = 90,
				Height = 30
			};
			button2.Click += delegate
			{
				foreach (Action item in saveActions)
				{
					item();
				}
				LoadCommandModelToGrid();
				Log("录入仪器指令已保存到 Command.ini。", important: true);
			};
			button.Click += delegate
			{
				form.Close();
			};
			flowLayoutPanel.Controls.Add(button);
			flowLayoutPanel.Controls.Add(button2);
			tableLayoutPanel.Controls.Add(tabControl, 0, 0);
			tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 1);
			form.Controls.Add(tableLayoutPanel);
			form.ShowDialog(this);
		}
		finally
		{
			if (form != null)
			{
				((IDisposable)form).Dispose();
			}
		}
	}

	private void ShowManualDebugDialog()
	{
		using Form form = new Form
		{
			Text = "调试",
			StartPosition = FormStartPosition.CenterParent,
			Width = 840,
			Height = 650,
			MinimizeBox = false,
			MaximizeBox = false,
			BackColor = SystemColors.Control
		};
		TabPage tabPage = BuildManualDebugTab();
		tabPage.Text = "手动";
		tabPage.Padding = new Padding(8);
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		tabControl.TabPages.Add(tabPage);
		form.FormClosing += delegate
		{
			if (_logManual.Parent != null)
			{
				_logManual.Parent.Controls.Remove(_logManual);
			}
		};
		form.Controls.Add(tabControl);
		form.ShowDialog(this);
	}

	private static GroupBox LegacyGroup(string title)
	{
		return new GroupBox
		{
			Text = title,
			Dock = DockStyle.Fill,
			BackColor = SystemColors.Control,
			Padding = new Padding(8, 18, 8, 8),
			Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold)
		};
	}

	private static TabPage LegacyTab(string title)
	{
		return new TabPage
		{
			Text = title,
			BackColor = SystemColors.Control,
			Padding = new Padding(8)
		};
	}

	private static void AddLegacyField(Control parent, string labelText, Control control, int x, int y, int width = 150, int labelWidth = 118)
	{
		Label value = new Label
		{
			Text = labelText,
			Left = x,
			Top = y,
			Width = labelWidth,
			Height = 22,
			TextAlign = ContentAlignment.MiddleLeft,
			Font = new Font("Microsoft YaHei UI", 9f)
		};
		control.Left = x;
		control.Top = y + 22;
		control.Width = width;
		control.Height = ((control is TextBox { Multiline: not false }) ? control.Height : 24);
		parent.Controls.Add(value);
		parent.Controls.Add(control);
	}

	private static void AddLegacyCheck(Control parent, CheckBox control, int x, int y, int width = 130)
	{
		control.Left = x;
		control.Top = y;
		control.Width = width;
		parent.Controls.Add(control);
	}

	private TabPage BuildLegacyBoardDevicePage()
	{
		TabPage tabPage = LegacyTab("采集板");
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		AddLegacyField(panel, "站号", CreateMirrorNumericUpDown(_addr), 82, 82, 100);
		AddLegacyField(panel, "VISA / COM", CreateMirrorComboBox(_com, SerialPort.GetPortNames()), 245, 82, 140);
		AddLegacyField(panel, "波特率", CreateMirrorComboBox(_boardBaud), 82, 162, 100);
		AddLegacyField(panel, "数据位", CreateMirrorComboBox(_boardDataBits), 245, 162, 100);
		AddLegacyField(panel, "校验位", CreateMirrorComboBox(_boardParity), 350, 162, 95);
		AddLegacyField(panel, "停止位", CreateMirrorComboBox(_boardStopBits), 82, 242, 100);
		AddLegacyField(panel, "板卡范围", CreateMirrorTextBox(_boardSlotMap), 245, 242, 200);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_useBoardChannel47, "启用4/7通道"), 82, 305);
		tabPage.Controls.Add(panel);
		return tabPage;
	}

	private TabPage BuildLegacySwitchDevicePage()
	{
		TabPage tabPage = LegacyTab("切换单元");
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		AddLegacyField(panel, "切换单元型号", CreateMirrorComboBox(_dmmModel), 70, 70, 170);
		AddLegacyField(panel, "VISA", CreateMirrorComboBox(_dmmAddr), 290, 70, 160);
		AddLegacyField(panel, "默认通道映射", CreateMirrorTextBox(_channelExpr), 70, 145, 170);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_useDaqChannel, "使用DAQ通道映射"), 290, 165, 155);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_multiDaq, "多台DAQ973A"), 70, 225, 145);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_daqSkipChannel47, "DAQ跳过4/7"), 290, 225);
		TextBox textBox = CreateMirrorTextBox(_daqProfiles, multiline: true, 82);
		textBox.ScrollBars = ScrollBars.Vertical;
		AddLegacyField(panel, "多DAQ映射", textBox, 70, 270, 380);
		tabPage.Controls.Add(panel);
		return tabPage;
	}

	private TabPage BuildLegacyOvenDevicePage()
	{
		TabPage tabPage = LegacyTab("烘箱");
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		AddLegacyField(panel, "烘箱型号", CreateMirrorComboBox(_compOvenModel, "GWSEBWT1670", "SIDAUMC1000"), 45, 28, 140);
		AddLegacyField(panel, "IP地址", CreateMirrorComboBox(_ovenIp), 45, 86, 190);
		AddLegacyField(panel, "端口号", CreateMirrorComboBox(_ovenPort), 45, 144, 120);
		AddLegacyField(panel, "保温时长(s)", CreateMirrorNumericUpDown(_compTempHoldSec), 45, 202, 100);
		AddLegacyField(panel, "VISA / COM", CreateMirrorComboBox(_ovenCom, SerialPort.GetPortNames()), 295, 64, 130);
		AddLegacyField(panel, "波特率", CreateMirrorComboBox(_ovenBaud), 295, 122, 100);
		AddLegacyField(panel, "数据位", CreateMirrorComboBox(_ovenDataBits), 295, 180, 100);
		AddLegacyField(panel, "校验位", CreateMirrorComboBox(_ovenParity), 295, 238, 100);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_compUseOven, "启用烘箱控制"), 45, 300, 140);
		tabPage.Controls.Add(panel);
		return tabPage;
	}

	private TabPage BuildLegacyPressureDevicePage()
	{
		TabPage tabPage = LegacyTab("压力控制器");
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		AddLegacyField(panel, "压力控制器型号", CreateMirrorComboBox(_pressureModel), 54, 58, 175);
		AddLegacyField(panel, "压力控制器-GPIB地址", CreateMirrorComboBox(_pressureAddr), 54, 135, 175);
		AddLegacyField(panel, "压力稳定时长(s)", CreateMirrorNumericUpDown(_stableSec), 54, 215, 95);
		AddLegacyField(panel, "GPIB端口", CreateMirrorComboBox(_pressureGpibPort), 305, 58, 100);
		AddLegacyField(panel, "GPIB地址", CreateMirrorComboBox(_pressureGpibAddress), 305, 116, 100);
		AddLegacyField(panel, "波动±kPa", CreateMirrorNumericUpDown(_stableTolKpa), 305, 174, 100);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_useGpib, "启用压力控制器"), 305, 250, 150);
		tabPage.Controls.Add(panel);
		return tabPage;
	}

	private TabPage BuildLegacyDmmDevicePage()
	{
		TabPage tabPage = LegacyTab("万用表");
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		AddLegacyField(panel, "万用表型号", CreateMirrorComboBox(_dmmModel), 70, 68, 180);
		AddLegacyField(panel, "VISA", CreateMirrorComboBox(_dmmAddr), 290, 68, 165);
		AddLegacyField(panel, "GPIB端口", CreateMirrorComboBox(_dmmGpibPort), 70, 146, 100);
		AddLegacyField(panel, "GPIB地址", CreateMirrorComboBox(_dmmGpibAddress), 290, 146, 100);
		AddLegacyField(panel, "默认映射", CreateMirrorTextBox(_channelExpr), 70, 224, 180);
		AddLegacyCheck(panel, CreateMirrorCheckBox(_daqSkipChannel47, "DAQ跳过4/7"), 290, 246);
		tabPage.Controls.Add(panel);
		return tabPage;
	}

	private TabPage BuildLegacyCommandPage(string title, string defaultModel, string defaultComm, string[] keys, List<Action> saveActions)
	{
		TabPage tabPage = LegacyTab(title);
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.FixedSingle,
			BackColor = SystemColors.Control
		};
		ComboBox model = ComboWith(defaultModel, _commands.Keys.Concat(new string[1] { defaultModel }).Distinct().ToArray());
		ComboBox control = ComboWith(defaultComm, "RS232", "GPIB", "TCP/IP");
		AddLegacyField(panel, title + "型号", model, 24, 28, 175);
		AddLegacyField(panel, "通信类型", control, 285, 28, 140);
		Dictionary<string, TextBox> boxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < keys.Length; i++)
		{
			int num = i % 2;
			int num2 = i / 2;
			int x = ((num == 0) ? 24 : 285);
			int y = 112 + num2 * 50;
			TextBox textBox = new TextBox
			{
				Width = 235
			};
			boxes[keys[i]] = textBox;
			AddLegacyField(panel, keys[i], textBox, x, y, 235, 190);
		}
		Fill();
		model.TextChanged += delegate
		{
			Fill();
		};
		saveActions.Add(delegate
		{
			SaveCommandEntriesForModel(model.Text, boxes.Select<KeyValuePair<string, TextBox>, KeyValuePair<string, string>>((KeyValuePair<string, TextBox> kv) => new KeyValuePair<string, string>(kv.Key, kv.Value.Text)));
		});
		tabPage.Controls.Add(panel);
		return tabPage;
		void Fill()
		{
			TryGetCommandSection(model.Text, out Dictionary<string, string> dict);
			string[] array = keys;
			foreach (string key in array)
			{
				if (boxes.TryGetValue(key, out TextBox value))
				{
					value.Text = ((dict != null && TryGetCommandValue(dict, key, out string value2)) ? value2.Trim().Trim('"') : "");
				}
			}
		}
	}

	private static TableLayoutPanel DialogGrid(int rows)
	{
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 5,
			RowCount = rows,
			Padding = new Padding(18),
			BackColor = Color.White
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
		for (int i = 1; i < 5; i++)
		{
			tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
		}
		for (int j = 0; j < rows; j++)
		{
			tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, (j == rows - 1) ? 56 : 42));
		}
		return tableLayoutPanel;
	}

	private static void AddDialogRow(TableLayoutPanel layout, int row, string title, params Control[] controls)
	{
		layout.Controls.Add(new Label
		{
			Text = title,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Padding = new Padding(0, 0, 8, 0),
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, 0, row);
		for (int i = 0; i < controls.Length && i < 4; i++)
		{
			controls[i].Dock = DockStyle.Fill;
			controls[i].Margin = new Padding(4);
			layout.Controls.Add(controls[i], i + 1, row);
		}
		if (controls.Length == 1)
		{
			layout.SetColumnSpan(controls[0], 4);
		}
	}

	private static void AddOkCancel(Form form, TableLayoutPanel layout, Action okAction)
	{
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft
		};
		Button button = new Button
		{
			Text = "确定",
			Width = 90,
			Height = 32
		};
		Button button2 = new Button
		{
			Text = "取消",
			Width = 90,
			Height = 32
		};
		button.Click += delegate
		{
			okAction();
			form.DialogResult = DialogResult.OK;
			form.Close();
		};
		button2.Click += delegate
		{
			form.DialogResult = DialogResult.Cancel;
			form.Close();
		};
		flowLayoutPanel.Controls.Add(button);
		flowLayoutPanel.Controls.Add(button2);
		layout.Controls.Add(flowLayoutPanel, 0, layout.RowCount - 1);
		layout.SetColumnSpan(flowLayoutPanel, 5);
		form.Controls.Add(layout);
		form.AcceptButton = button;
		form.CancelButton = button2;
	}

	private static NumericUpDown CloneNumeric(NumericUpDown source)
	{
		return new NumericUpDown
		{
			Minimum = source.Minimum,
			Maximum = source.Maximum,
			DecimalPlaces = source.DecimalPlaces,
			Increment = source.Increment,
			Value = source.Value,
			Width = Math.Max(source.Width, 80)
		};
	}

	private static void CopyNumeric(NumericUpDown from, NumericUpDown to)
	{
		to.Value = Math.Min(to.Maximum, Math.Max(to.Minimum, from.Value));
	}

	private static void AddInline(FlowLayoutPanel panel, string text, Control control)
	{
		panel.Controls.Add(new Label
		{
			Text = text,
			AutoSize = true,
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding(8, 6, 2, 0),
			Margin = new Padding(0, 0, 0, 0)
		});
		control.Margin = new Padding(0, 1, 10, 0);
		panel.Controls.Add(control);
	}

	private static void AddGrid(TableLayoutPanel grid, int col, int row, string text)
	{
		grid.Controls.Add(new Label
		{
			Text = text,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Padding = new Padding(0, 0, 4, 0)
		}, col, row);
	}

	private TabPage BuildManualDebugTab()
	{
		TabPage tabPage = new TabPage("手动调试")
		{
			BackColor = Color.FromArgb(245, 247, 251),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 68f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 32f));
		ComboBox pressureVisa = VisaCombo(_pressureAddr.Text);
		ComboBox pressureModel = new ComboBox
		{
			Dock = DockStyle.Fill,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		pressureModel.Items.AddRange(new object[7] { "DRUCK-PACE5000", "DRUCK-PACE6000", "FLUKE-7250", "FLUKE-6270A", "WIKA-CPC6050", "WIKA-CPC8000", "ConST-860" });
		pressureModel.Text = _pressureModel.Text;
		ComboBox pressureUnit = new ComboBox
		{
			Dock = DockStyle.Fill,
			DropDownStyle = ComboBoxStyle.DropDownList
		};
		pressureUnit.Items.AddRange(new object[2] { "kPa", "psi" });
		pressureUnit.Text = (string.Equals(_pressureUnit.Text, "psi", StringComparison.OrdinalIgnoreCase) ? "psi" : "kPa");
		NumericUpDown pressureValue = new NumericUpDown
		{
			Dock = DockStyle.Fill,
			Minimum = -100000m,
			Maximum = 100000m,
			DecimalPlaces = 3,
			Value = _pfull.Value
		};
		TextBox pressureRead = new TextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true
		};
		TextBox pressureRaw = new TextBox
		{
			Dock = DockStyle.Fill,
			Text = "*IDN?"
		};
		GroupBox groupBox = Card("压力控制器手动调试");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4,
			RowCount = 6,
			Padding = new Padding(12)
		};
		for (int i = 0; i < 4; i++)
		{
			tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle((i % 2 == 0) ? SizeType.Absolute : SizeType.Percent, (i % 2 == 0) ? 72 : 50));
		}
		AddFormCell(tableLayoutPanel2, 0, 0, "VISA", pressureVisa);
		AddFormCell(tableLayoutPanel2, 0, 2, "型号", pressureModel);
		AddFormCell(tableLayoutPanel2, 1, 0, "单位", pressureUnit);
		AddFormCell(tableLayoutPanel2, 1, 2, "压力值", pressureValue);
		AddFormCell(tableLayoutPanel2, 2, 0, "读数", pressureRead);
		AddFormCell(tableLayoutPanel2, 2, 2, "原始命令", pressureRaw);
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight
		};
		Button button = new Button
		{
			Text = "读取型号",
			Width = 92,
			Height = 30
		};
		Button button2 = new Button
		{
			Text = "加压/设压",
			Width = 92,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button3 = new Button
		{
			Text = "读取压力",
			Width = 92,
			Height = 30
		};
		Button button4 = new Button
		{
			Text = "泄压",
			Width = 92,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button5 = new Button
		{
			Text = "发送原始",
			Width = 92,
			Height = 30
		};
		flowLayoutPanel.Controls.AddRange(new Control[5] { button, button2, button3, button4, button5 });
		tableLayoutPanel2.Controls.Add(flowLayoutPanel, 0, 3);
		tableLayoutPanel2.SetColumnSpan(flowLayoutPanel, 4);
		groupBox.Controls.Add(tableLayoutPanel2);
		button.Click += async delegate
		{
			await WithPressure(delegate(VisaInstrument inst)
			{
				Log("压力控制器型号：" + inst.Query(CommandFor(pressureModel.Text, "MachineType", "*IDN?")), important: true);
				return Task.CompletedTask;
			});
		};
		button2.Click += async delegate
		{
			await WithPressure(delegate(VisaInstrument inst)
			{
				double value2 = (double)pressureValue.Value;
				double num2 = ConvertPressureToKpa(value2, pressureUnit.Text);
				inst.Write(CommandFor(pressureModel.Text, "SetPressure", "*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON", num2.ToString("0.######", CultureInfo.InvariantCulture)));
				Log("手动设压：" + FormatPressureValue(value2, pressureUnit.Text), important: true);
				return Task.CompletedTask;
			});
		};
		button3.Click += async delegate
		{
			await WithPressure(delegate(VisaInstrument inst)
			{
				double value2 = inst.QueryNumber(CommandFor(pressureModel.Text, "ReadPressure", "*CLS;SENS?"));
				double user = ConvertPressureFromKpa(value2, pressureUnit.Text);
				BeginInvoke(delegate
				{
					pressureRead.Text = user.ToString("0.######", CultureInfo.InvariantCulture);
				});
				Log("手动读取压力：" + FormatPressureValue(user, pressureUnit.Text, 6), important: true);
				return Task.CompletedTask;
			});
		};
		button4.Click += async delegate
		{
			await WithPressure(delegate(VisaInstrument inst)
			{
				inst.Write(CommandFor(pressureModel.Text, "Vent", "*CLS;:Sour:Vent 1;:OUTPUT OFF"));
				Log("手动泄压命令已发送", important: true);
				return Task.CompletedTask;
			});
		};
		button5.Click += async delegate
		{
			await WithPressure(delegate(VisaInstrument inst)
			{
				string text = pressureRaw.Text.Trim();
				if (text.EndsWith("?"))
				{
					Log("压力原始返回：" + inst.Query(text), important: true);
				}
				else
				{
					inst.Write(text);
				}
				return Task.CompletedTask;
			});
		};
		ComboBox daqVisa = VisaCombo(_dmmAddr.Text);
		ComboBox daqModel = new ComboBox
		{
			Dock = DockStyle.Fill,
			DropDownStyle = ComboBoxStyle.DropDown
		};
		daqModel.Items.AddRange(new object[3] { "Keysight-DAQ973A", "Keysight-34970A", "Keysight-34461" });
		daqModel.Text = _dmmModel.Text;
		TextBox daqChannel = new TextBox
		{
			Dock = DockStyle.Fill,
			Text = "101"
		};
		TextBox daqValue = new TextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true
		};
		TextBox daqRaw = new TextBox
		{
			Dock = DockStyle.Fill,
			Text = "READ?"
		};
		GroupBox groupBox2 = Card("DAQ973A / DMM 手动采集");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4,
			RowCount = 5,
			Padding = new Padding(12)
		};
		for (int num = 0; num < 4; num++)
		{
			tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle((num % 2 == 0) ? SizeType.Absolute : SizeType.Percent, (num % 2 == 0) ? 72 : 50));
		}
		AddFormCell(tableLayoutPanel3, 0, 0, "VISA", daqVisa);
		AddFormCell(tableLayoutPanel3, 0, 2, "型号", daqModel);
		AddFormCell(tableLayoutPanel3, 1, 0, "通道", daqChannel);
		AddFormCell(tableLayoutPanel3, 1, 2, "读值", daqValue);
		AddFormCell(tableLayoutPanel3, 2, 0, "原始命令", daqRaw);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill
		};
		Button button6 = new Button
		{
			Text = "读取型号",
			Width = 92,
			Height = 30
		};
		Button button7 = new Button
		{
			Text = "读电压",
			Width = 92,
			Height = 30,
			BackColor = Color.FromArgb(20, 184, 166)
		};
		Button button8 = new Button
		{
			Text = "闭合通道",
			Width = 92,
			Height = 30
		};
		Button button9 = new Button
		{
			Text = "打开通道",
			Width = 92,
			Height = 30
		};
		Button button10 = new Button
		{
			Text = "发送原始",
			Width = 92,
			Height = 30
		};
		flowLayoutPanel2.Controls.AddRange(new Control[5] { button6, button7, button8, button9, button10 });
		tableLayoutPanel3.Controls.Add(flowLayoutPanel2, 0, 3);
		tableLayoutPanel3.SetColumnSpan(flowLayoutPanel2, 4);
		groupBox2.Controls.Add(tableLayoutPanel3);
		button6.Click += async delegate
		{
			await WithDaq(delegate(VisaInstrument inst)
			{
				Log("DAQ型号：" + inst.Query(CommandFor(daqModel.Text, "MachineType", "*IDN?")), important: true);
				return Task.CompletedTask;
			});
		};
		button8.Click += async delegate
		{
			await WithDaq(delegate(VisaInstrument inst)
			{
				inst.Write(CommandFor(daqModel.Text, "Close", "ROUT:CLOS (@9999)", daqChannel.Text.Trim()));
				return Task.CompletedTask;
			});
		};
		button9.Click += async delegate
		{
			await WithDaq(delegate(VisaInstrument inst)
			{
				inst.Write(CommandFor(daqModel.Text, "Open", "ROUT:OPEN (@9999)", daqChannel.Text.Trim()));
				return Task.CompletedTask;
			});
		};
		button7.Click += async delegate
		{
			await WithDaq(delegate(VisaInstrument inst)
			{
				string text = daqChannel.Text.Trim();
				inst.Write(CommandFor(daqModel.Text, "Close", "ROUT:CLOS (@9999)", text));
				inst.Write(CommandFor(daqModel.Text, "SetVol", "CONF:VOLT (@9999)", text));
				double v = inst.QueryNumber(CommandFor(daqModel.Text, "ReadValue", "READ?"));
				try
				{
					inst.Write(CommandFor(daqModel.Text, "Open", "ROUT:OPEN (@9999)", text));
				}
				catch
				{
				}
				BeginInvoke(delegate
				{
					daqValue.Text = v.ToString("0.######", CultureInfo.InvariantCulture);
				});
				Log($"手动DAQ CH{text} = {v:0.######} V", important: true);
				return Task.CompletedTask;
			});
		};
		button10.Click += async delegate
		{
			await WithDaq(delegate(VisaInstrument inst)
			{
				string text = daqRaw.Text.Trim();
				if (text.EndsWith("?"))
				{
					Log("DAQ原始返回：" + inst.Query(text), important: true);
				}
				else
				{
					inst.Write(text);
				}
				return Task.CompletedTask;
			});
		};
		GroupBox value = BuildOvenManualCard("烘箱手动调试", delegate(string msg)
		{
			Log(msg, important: true);
		});
		GroupBox groupBox3 = Card("板卡快捷调试");
		FlowLayoutPanel flowLayoutPanel3 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12),
			WrapContents = true
		};
		NumericUpDown dbgSlot = new NumericUpDown
		{
			Minimum = 1m,
			Maximum = 255m,
			Value = 1m,
			Width = 80
		};
		Button button11 = new Button
		{
			Text = "AA通信",
			Width = 90,
			Height = 30
		};
		Button button12 = new Button
		{
			Text = "读原始02",
			Width = 90,
			Height = 30
		};
		Button button13 = new Button
		{
			Text = "读补偿12",
			Width = 90,
			Height = 30
		};
		Button button14 = new Button
		{
			Text = "进OWI63",
			Width = 90,
			Height = 30
		};
		Button button15 = new Button
		{
			Text = "退OWI61",
			Width = 90,
			Height = 30
		};
		Add(flowLayoutPanel3, "Slot", dbgSlot, button11, button12, button13, button14, button15);
		groupBox3.Controls.Add(flowLayoutPanel3);
		button11.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(170, Array.Empty<byte>(), 4, ct);
			});
		};
		button12.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(2, new byte[1] { (byte)dbgSlot.Value }, 13, ct);
			});
		};
		button13.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(18, new byte[1] { (byte)dbgSlot.Value }, 13, ct);
			});
		};
		button14.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(99, new byte[1] { (byte)dbgSlot.Value }, 5, ct);
			});
		};
		button15.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(97, new byte[1] { (byte)dbgSlot.Value }, 5, ct);
			});
		};
		TabControl tabControl = new TabControl
		{
			Dock = DockStyle.Fill
		};
		TabPage tabPage2 = new TabPage("压力控制器")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage3 = new TabPage("DAQ / DMM")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage4 = new TabPage("烘箱")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		TabPage tabPage5 = new TabPage("板卡")
		{
			BackColor = Color.White,
			Padding = new Padding(8)
		};
		tabPage2.Controls.Add(groupBox);
		tabPage3.Controls.Add(groupBox2);
		tabPage4.Controls.Add(value);
		tabPage5.Controls.Add(groupBox3);
		tabControl.TabPages.Add(tabPage2);
		tabControl.TabPages.Add(tabPage3);
		tabControl.TabPages.Add(tabPage4);
		tabControl.TabPages.Add(tabPage5);
		GroupBox groupBox4 = Card("手动调试实时日志");
		groupBox4.Controls.Add(_logManual);
		tableLayoutPanel.Controls.Add(BuildDeviceStatusPanel(), 0, 0);
		tableLayoutPanel.Controls.Add(tabControl, 0, 1);
		tableLayoutPanel.Controls.Add(groupBox4, 0, 2);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
		ComboBox VisaCombo(string text)
		{
			ComboBox comboBox = new ComboBox
			{
				DropDownStyle = ComboBoxStyle.DropDown,
				Dock = DockStyle.Fill
			};
			SortedSet<string> sortedSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { text, _pressureAddr.Text, _dmmAddr.Text };
			foreach (string item in from x in _pressureAddr.Items.Cast<object>().Select(Convert.ToString)
				where !string.IsNullOrWhiteSpace(x)
				select x)
			{
				sortedSet.Add(item);
			}
			foreach (string item2 in from x in _dmmAddr.Items.Cast<object>().Select(Convert.ToString)
				where !string.IsNullOrWhiteSpace(x)
				select x)
			{
				sortedSet.Add(item2);
			}
			foreach (DataGridViewRow item3 in (IEnumerable)_daqProfileGrid.Rows)
			{
				if (!item3.IsNewRow)
				{
					sortedSet.Add(Convert.ToString(item3.Cells["Visa"].Value) ?? "");
				}
			}
			foreach (string item4 in sortedSet.Where((string x) => !string.IsNullOrWhiteSpace(x)))
			{
				comboBox.Items.Add(item4);
			}
			comboBox.Text = text;
			return comboBox;
		}
		async Task WithDaq(Func<VisaInstrument, Task> action)
		{
			Log("手动DAQ调试：开始连接 " + daqVisa.Text, important: true);
			try
			{
				await Task.Run(async delegate
				{
					using VisaInstrument inst = new VisaInstrument("手动-DAQ", daqVisa.Text.Trim(), Log);
					inst.Open();
					await action(inst);
				});
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Log("手动DAQ调试失败：" + ex2.Message, important: true);
			}
		}
		async Task WithPressure(Func<VisaInstrument, Task> action)
		{
			Log("手动压力调试：开始连接 " + pressureVisa.Text, important: true);
			try
			{
				await Task.Run(async delegate
				{
					using VisaInstrument inst = new VisaInstrument("手动-压力", pressureVisa.Text.Trim(), Log);
					inst.Open();
					await action(inst);
				});
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Log("手动压力调试失败：" + ex2.Message, important: true);
			}
		}
	}

	private TabPage BuildDeviceTab()
	{
		TabPage tabPage = new TabPage("设备/DAQ配置")
		{
			BackColor = Color.FromArgb(246, 248, 251),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2,
			ColumnCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 245f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		GroupBox groupBox = Card("板卡串口 / 下位机通信");
		TableLayoutPanel tableLayoutPanel2 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 8,
			Padding = new Padding(14, 12, 14, 6)
		};
		for (int i = 0; i < 8; i++)
		{
			tableLayoutPanel2.ColumnStyles.Add(new ColumnStyle((i % 2 == 0) ? SizeType.Absolute : SizeType.Percent, (i % 2 == 0) ? 66 : 25));
		}
		AddFormRow(tableLayoutPanel2, 0, "站号", _addr, "COM", _com, "波特率", _boardBaud, "超时ms", _timeout);
		AddFormRow(tableLayoutPanel2, 1, "数据位", _boardDataBits, "校验", _boardParity, "停止位", _boardStopBits, "", _openSerial);
		AddFormRow(tableLayoutPanel2, 2, "板卡范围", _boardSlotMapDevice, "", new Label
		{
			Text = "例：1=1-80;2=81-160，Slot81会发到板卡2的本地Slot1",
			AutoSize = true
		}, "", new Label(), "", new Label());
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			Height = 42,
			Padding = new Padding(14, 4, 0, 4)
		};
		Button button = new Button
		{
			Text = "打开/关闭板卡串口",
			Width = 148,
			Height = 30,
			BackColor = Color.FromArgb(248, 113, 113),
			ForeColor = Color.White
		};
		button.Click += delegate
		{
			ToggleSerial();
		};
		flowLayoutPanel.Controls.Add(_refreshCom);
		flowLayoutPanel.Controls.Add(button);
		groupBox.Controls.Add(tableLayoutPanel2);
		groupBox.Controls.Add(flowLayoutPanel);
		GroupBox groupBox2 = Card("压力控制器 / GPIB");
		TableLayoutPanel tableLayoutPanel3 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 4,
			Padding = new Padding(14, 12, 14, 6)
		};
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel3, 0, "启用", _useGpib, "型号", _pressureModel);
		AddFormRow2(tableLayoutPanel3, 1, "GPIB端口", _pressureGpibPort, "GPIB地址", _pressureGpibAddress);
		AddFormRow2(tableLayoutPanel3, 2, "VISA", _pressureAddr, "稳压±kPa", _stableTolKpa);
		AddFormRow2(tableLayoutPanel3, 3, "稳压s", _stableSec, "延时s", _settleSec);
		groupBox2.Controls.Add(tableLayoutPanel3);
		GroupBox groupBox3 = Card("DAQ973A / DMM 采集配置");
		TableLayoutPanel tableLayoutPanel4 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			ColumnCount = 1,
			Padding = new Padding(14, 10, 14, 10)
		};
		tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Absolute, 142f));
		tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		tableLayoutPanel4.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		TableLayoutPanel tableLayoutPanel5 = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 4
		};
		tableLayoutPanel5.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel5.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel5.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
		tableLayoutPanel5.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		AddFormRow2(tableLayoutPanel5, 0, "型号", _dmmModel, "默认VISA", _dmmAddr);
		AddFormRow2(tableLayoutPanel5, 1, "GPIB端口", _dmmGpibPort, "GPIB地址", _dmmGpibAddress);
		AddFormRow2(tableLayoutPanel5, 2, "采集通道", _useDaqChannel, "多台DAQ", _multiDaq);
		AddFormRow2(tableLayoutPanel5, 3, "DAQ跳过", _daqSkipChannel47, "默认映射", _channelExpr);
		FlowLayoutPanel flowLayoutPanel2 = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(0, 2, 0, 2)
		};
		flowLayoutPanel2.Controls.Add(_refreshVisa);
		flowLayoutPanel2.Controls.Add(new Label
		{
			Text = "手动通道覆盖",
			AutoSize = true,
			Padding = new Padding(10, 7, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		});
		flowLayoutPanel2.Controls.Add(_daqChannelOverrideMap);
		flowLayoutPanel2.Controls.Add(_fillChannelSequence);
		flowLayoutPanel2.Controls.Add(_applyChannelMapDevice);
		flowLayoutPanel2.Controls.Add(new Label
		{
			Text = "格式：工位=DAQ通道，例如 1=101;33=213 或 1-20=101-120;21-40=201-220",
			AutoSize = true,
			Padding = new Padding(10, 7, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		});
		tableLayoutPanel4.Controls.Add(tableLayoutPanel5, 0, 0);
		tableLayoutPanel4.Controls.Add(flowLayoutPanel2, 0, 1);
		tableLayoutPanel4.Controls.Add(_daqProfileGrid, 0, 2);
		groupBox3.Controls.Add(tableLayoutPanel4);
		GroupBox groupBox4 = Card("标定参数 / 写入策略");
		TableLayoutPanel tableLayoutPanel6 = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 2,
			Padding = new Padding(14, 12, 14, 6)
		};
		tableLayoutPanel6.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118f));
		tableLayoutPanel6.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tableLayoutPanel6.RowCount = 5;
		tableLayoutPanel6.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel6.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel6.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel6.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
		tableLayoutPanel6.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "标定型号/压力点",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight
		}, 0, 0);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "在【标定运行】页选择型号，P0/Pmid/Pfull 会自动带出",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft
		}, 1, 0);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "保留温度",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight
		}, 0, 1);
		tableLayoutPanel6.Controls.Add(_preserveTempCoe, 1, 1);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "写板卡",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight
		}, 0, 2);
		tableLayoutPanel6.Controls.Add(_writeBoard, 1, 2);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "写后复测",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight
		}, 0, 3);
		tableLayoutPanel6.Controls.Add(_verifyAfterWrite, 1, 3);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "运行模式",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.TopRight
		}, 0, 4);
		tableLayoutPanel6.Controls.Add(new Label
		{
			Text = "默认：原程序逐工位闭环；开启批量稳压=同一压力点批量扫表，非原程序节奏。",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.TopLeft,
			ForeColor = Color.FromArgb(71, 85, 105)
		}, 1, 4);
		groupBox4.Controls.Add(tableLayoutPanel6);
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(groupBox2, 1, 0);
		tableLayoutPanel.Controls.Add(groupBox3, 0, 1);
		tableLayoutPanel.Controls.Add(groupBox4, 1, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private void AddFormRow(TableLayoutPanel table, int row, string l1, Control c1, string l2, Control c2, string l3, Control c3, string l4, Control c4)
	{
		while (table.RowStyles.Count <= row)
		{
			table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		}
		table.RowCount = Math.Max(table.RowCount, row + 1);
		AddFormCell(table, row, 0, l1, c1);
		AddFormCell(table, row, 2, l2, c2);
		AddFormCell(table, row, 4, l3, c3);
		AddFormCell(table, row, 6, l4, c4);
	}

	private void AddFormRow2(TableLayoutPanel table, int row, string l1, Control c1, string l2, Control c2)
	{
		while (table.RowStyles.Count <= row)
		{
			table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
		}
		table.RowCount = Math.Max(table.RowCount, row + 1);
		AddFormCell(table, row, 0, l1, c1);
		AddFormCell(table, row, 2, l2, c2);
	}

	private static void AddFormCell(TableLayoutPanel table, int row, int col, string label, Control control)
	{
		table.Controls.Add(new Label
		{
			Text = label,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		}, col, row);
		control.Dock = DockStyle.Fill;
		control.Margin = new Padding(4, 4, 12, 4);
		table.Controls.Add(control, col + 1, row);
	}

	private TabPage BuildBoardCommandTab()
	{
		TabPage tabPage = new TabPage("板卡指令")
		{
			BackColor = Color.FromArgb(245, 247, 251),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 175f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		GroupBox groupBox = Card("手动指令 / 快速诊断");
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(12),
			WrapContents = true
		};
		Add(flowLayoutPanel, "Slot", _manualSlot, "功能", _manualFunction, "自定义Payload HEX", _manualPayload, "期望长度", _manualExpectedLen, _manualSend);
		Add(flowLayoutPanel, _quickPing, _quickReadRaw, _quickReadCal, _quickEnterOwi, _quickExitOwi);
		groupBox.Controls.Add(flowLayoutPanel);
		TextBox control = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			Font = new Font("Consolas", 10f),
			Text = "协议速查\r\n\r\nAA：通信测试\r\n02 Slot：读原始数据\r\n12 Slot：读补偿后数据\r\n63 Slot：进入OWI\r\n61 Slot：退出OWI\r\n76 Slot：读IIC地址\r\n11 Slot + 10*Int32LE：写标定/补偿系数\r\n\r\n帧格式：站号 功能码 数据... CRC_L CRC_H\r\nCRC：Modbus CRC16，小端。"
		};
		tableLayoutPanel.Controls.Add(groupBox, 0, 0);
		tableLayoutPanel.Controls.Add(control, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildDataTab()
	{
		TabPage tabPage = new TabPage("实时日志")
		{
			BackColor = Color.FromArgb(245, 247, 251),
			Padding = new Padding(12)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 2
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(8),
			BackColor = Color.White
		};
		Button button = new Button
		{
			Text = "清空显示",
			Width = 90,
			Height = 30
		};
		Button button2 = new Button
		{
			Text = "打开当前日志",
			Width = 110,
			Height = 30
		};
		flowLayoutPanel.Controls.Add(_openLogDir);
		flowLayoutPanel.Controls.Add(button2);
		flowLayoutPanel.Controls.Add(button);
		flowLayoutPanel.Controls.Add(new Label
		{
			Text = "  这里显示完整实时日志；运行页下方也同步显示。",
			AutoSize = true,
			Padding = new Padding(10, 8, 0, 0),
			ForeColor = Color.FromArgb(71, 85, 105)
		});
		button.Click += delegate
		{
			_log.Clear();
			_logFull.Clear();
		};
		button2.Click += delegate
		{
			if (File.Exists(_logFile))
			{
				Process.Start(new ProcessStartInfo(_logFile)
				{
					UseShellExecute = true
				});
			}
		};
		tableLayoutPanel.Controls.Add(flowLayoutPanel, 0, 0);
		tableLayoutPanel.Controls.Add(_logFull, 0, 1);
		tabPage.Controls.Add(tableLayoutPanel);
		return tabPage;
	}

	private TabPage BuildHelpTab()
	{
		TabPage tabPage = new TabPage("说明")
		{
			BackColor = Color.White,
			Padding = new Padding(18)
		};
		tabPage.Controls.Add(new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			Font = new Font("Microsoft YaHei UI", 10f),
			Text = "F40标定\r\n\r\n现场版核心流程：\r\n1. 加载原始补偿 CSV 后，标定表默认固定为4个可用通道32工位：1-8、9-16、17-24、33-40。\r\n2. 表格支持选中后 Ctrl+C 复制原始数据，方便手工核对和复用。\r\n3. “复用原始数据”可按 41-48=1-8;57-64=33-40 这类格式，把后续Slot的原补偿数据复制到可写工位。\r\n4. “手动通道覆盖”可按 1=101;9=102 或 1-20=101-120 格式指定DAQ采集通道。\r\n5. 写系数使用原程序兼容策略：0x63/0x61 固定 Slot1，0x11 写目标 Slot。\r\n6. 支持单工位按顺序标定，也支持统一加压批量标定。"
		});
		return tabPage;
	}

	private GroupBox Card(string title)
	{
		return new GroupBox
		{
			Text = title,
			Dock = DockStyle.Fill,
			Padding = new Padding(10, 12, 10, 10),
			BackColor = IndustrialSurface,
			ForeColor = IndustrialText,
			Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
		};
	}

	private bool IsConsoleTextBox(TextBox textBox)
	{
		return ReferenceEquals(textBox, _log)
			|| ReferenceEquals(textBox, _logFull)
			|| ReferenceEquals(textBox, _logManual)
			|| ReferenceEquals(textBox, _logComp)
			|| ReferenceEquals(textBox, _logCompManual);
	}

	private void StyleControls(Control parent)
	{
		foreach (Control control4 in parent.Controls)
		{
			Control control2 = control4;
			Control control3 = control2;
			if (!(control3 is Button button))
			{
				if (!(control3 is DataGridView dataGridView))
				{
					if (!(control3 is TextBox textBox))
					{
						if (!(control3 is ComboBox comboBox))
						{
							if (!(control3 is NumericUpDown numericUpDown))
							{
								if (!(control3 is TabControl tabControl))
								{
									if (!(control3 is TabPage tabPage))
									{
										if (!(control3 is Label label))
										{
											if (!(control3 is CheckBox checkBox))
											{
												if (!(control3 is MenuStrip menuStrip))
												{
													if (control3 is Panel panel && (panel.BackColor == Color.White || panel.BackColor == SystemColors.Control || panel.BackColor == Color.FromArgb(248, 250, 252) || panel.BackColor == Color.FromArgb(245, 248, 250) || panel.BackColor == Color.FromArgb(245, 247, 251)))
													{
														panel.BackColor = IndustrialWorkspace;
													}
												}
												else
												{
													menuStrip.BackColor = IndustrialHeader;
													menuStrip.ForeColor = IndustrialText;
												}
											}
											else
											{
												checkBox.ForeColor = IndustrialText;
												checkBox.BackColor = Color.Transparent;
											}
										}
										else if (label.ForeColor == SystemColors.ControlText || label.ForeColor == Color.FromArgb(15, 23, 42) || label.ForeColor == Color.FromArgb(30, 41, 59) || label.ForeColor == Color.FromArgb(71, 85, 105))
										{
											label.ForeColor = IndustrialText;
										}
									}
									else
									{
										tabPage.BackColor = IndustrialWorkspace;
										tabPage.ForeColor = IndustrialText;
									}
								}
								else
								{
									tabControl.BackColor = IndustrialWorkspace;
									tabControl.ForeColor = IndustrialText;
								}
							}
							else
							{
								numericUpDown.BorderStyle = BorderStyle.FixedSingle;
								numericUpDown.BackColor = Color.White;
								numericUpDown.ForeColor = IndustrialText;
							}
						}
						else
						{
							comboBox.FlatStyle = FlatStyle.Flat;
							comboBox.BackColor = Color.White;
							comboBox.ForeColor = IndustrialText;
						}
					}
					else
					{
						if (IsConsoleTextBox(textBox))
						{
							textBox.BorderStyle = BorderStyle.FixedSingle;
							textBox.BackColor = IndustrialConsole;
							textBox.ForeColor = IndustrialConsoleText;
						}
						else
						{
							if (!textBox.Multiline || textBox.Height < 40)
							{
								textBox.BorderStyle = BorderStyle.FixedSingle;
							}
							textBox.BackColor = (textBox.ReadOnly ? IndustrialSurfaceAlt : Color.White);
							textBox.ForeColor = IndustrialText;
						}
					}
				}
				else
				{
					dataGridView.BackgroundColor = IndustrialSurfaceAlt;
					dataGridView.BorderStyle = BorderStyle.FixedSingle;
					dataGridView.EnableHeadersVisualStyles = false;
					dataGridView.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(221, 229, 233);
					dataGridView.ColumnHeadersDefaultCellStyle.ForeColor = IndustrialText;
					dataGridView.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 229, 233);
					dataGridView.ColumnHeadersDefaultCellStyle.SelectionForeColor = IndustrialText;
					dataGridView.RowHeadersDefaultCellStyle.BackColor = IndustrialSurfaceAlt;
					dataGridView.GridColor = Color.FromArgb(184, 191, 197);
					dataGridView.RowTemplate.Height = Math.Max(dataGridView.RowTemplate.Height, 22);
					dataGridView.ColumnHeadersHeight = Math.Max(dataGridView.ColumnHeadersHeight, 26);
					dataGridView.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
					dataGridView.DefaultCellStyle.BackColor = Color.White;
					dataGridView.DefaultCellStyle.ForeColor = IndustrialText;
					dataGridView.DefaultCellStyle.SelectionBackColor = IndustrialAccent;
					dataGridView.DefaultCellStyle.SelectionForeColor = Color.White;
					dataGridView.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 244);
				}
			}
			else
			{
				button.FlatStyle = FlatStyle.Flat;
				button.FlatAppearance.BorderColor = Color.FromArgb(151, 161, 169);
				button.FlatAppearance.MouseOverBackColor = Color.FromArgb(214, 222, 227);
				button.FlatAppearance.MouseDownBackColor = Color.FromArgb(198, 209, 216);
				if (button.BackColor == SystemColors.Control || button.BackColor == Color.Empty)
				{
					button.BackColor = IndustrialSurface;
				}
				if (button.BackColor == Color.FromArgb(22, 163, 74) || button.BackColor == Color.FromArgb(20, 184, 166) || button.BackColor == IndustrialSuccess || button.BackColor == IndustrialDanger || button.BackColor == IndustrialWarning || button.BackColor == IndustrialAccent)
				{
					button.ForeColor = Color.White;
				}
				else if (button.BackColor == Color.FromArgb(254, 226, 226))
				{
					button.ForeColor = Color.FromArgb(185, 28, 28);
				}
				else if (button.ForeColor != Color.FromArgb(220, 38, 38))
				{
					button.ForeColor = IndustrialText;
				}
			}
			if (control4.HasChildren)
			{
				StyleControls(control4);
			}
		}
	}

	private static void Add(FlowLayoutPanel p, params object[] items)
	{
		foreach (object obj in items)
		{
			if (obj is string text)
			{
				p.Controls.Add(new Label
				{
					Text = text,
					AutoSize = true,
					Padding = new Padding(10, 7, 0, 0)
				});
			}
			else if (obj is Control value)
			{
				p.Controls.Add(value);
			}
		}
		Control.ControlCollection controls = p.Controls;
		p.SetFlowBreak(controls[controls.Count - 1], value: true);
	}

	private void SetupGrid()
	{
		_grid.DataSource = _rows;
		_grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
		_grid.MultiSelect = true;
		_grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText;
		_grid.RowTemplate.Height = 26;
		_grid.ColumnHeadersHeight = 30;
		_grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
		_grid.ScrollBars = ScrollBars.Both;
		_grid.Columns.Add(new DataGridViewCheckBoxColumn
		{
			DataPropertyName = "Selected",
			HeaderText = "选",
			Width = 42,
			SortMode = DataGridViewColumnSortMode.NotSortable,
			ToolTipText = "点击表头全选/全不选；Shift+点击可批量勾选范围"
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "Slot",
			HeaderText = "Slot",
			Width = 58,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "Serial",
			HeaderText = "序列号",
			Width = 150,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "TestResult",
			HeaderText = "原补偿",
			Width = 65,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "DmmAddress",
			HeaderText = "DMM/DAQ地址",
			Width = 155,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "Channel",
			HeaderText = "DMM通道",
			Width = 82
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "PreZeroV",
			HeaderText = "写前低V",
			Width = 82,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "PreFullV",
			HeaderText = "写前满V",
			Width = 82,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "ZeroV",
			HeaderText = "写后低V",
			Width = 82,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "FullV",
			HeaderText = "写后满V",
			Width = 82,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "PostMidV",
			HeaderText = "写后中V",
			Width = 82,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "NewMinPercent",
			HeaderText = "新低%",
			Width = 78,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "NewMidPercent",
			HeaderText = "新中%",
			Width = 78,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "NewMaxPercent",
			HeaderText = "新满%",
			Width = 78,
			DefaultCellStyle = 
			{
				Format = "0.######"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "LinearityPercent",
			HeaderText = "线性%",
			Width = 78,
			DefaultCellStyle = 
			{
				Format = "0.###"
			}
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "WriteResult",
			HeaderText = "写系数",
			Width = 105,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "CoefficientsText",
			HeaderText = "10系数",
			Width = 340,
			ReadOnly = true
		});
		_grid.Columns.Add(new DataGridViewTextBoxColumn
		{
			DataPropertyName = "Status",
			HeaderText = "状态",
			Width = 190,
			AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
		});
		_grid.CellFormatting += FormatCalibrationGridCell;
		_grid.CurrentCellDirtyStateChanged += CommitCalibrationGridCheckboxEdit;
		_grid.CellClick += HandleCalibrationGridCellClick;
		_grid.CellContentClick += HandleCalibrationGridCellContentClick;
		_grid.KeyDown += HandleCalibrationGridKeyDown;
	}

	private void CommitCalibrationGridCheckboxEdit(object? sender, EventArgs e)
	{
		if (_grid.IsCurrentCellDirty)
		{
			DataGridViewCell currentCell = _grid.CurrentCell;
			if (currentCell != null && currentCell.ColumnIndex == 0)
			{
				_grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		}
	}

	private void HandleCalibrationGridCellClick(object? sender, DataGridViewCellEventArgs e)
	{
		if (e.RowIndex == -1 && e.ColumnIndex == 0)
		{
			SetCalibrationRowsSelected(_rows, _rows.Any((F40SlotRow r) => !r.Selected));
		}
	}

	private void HandleCalibrationGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
	{
		if (e.RowIndex >= 0 && e.ColumnIndex == 0)
		{
			BeginInvoke(delegate
			{
				ApplyCalibrationCheckboxBatch(e.RowIndex);
			});
		}
	}

	private void HandleCalibrationGridKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Control && e.KeyCode == Keys.A)
		{
			SetCalibrationRowsSelected(_rows, selected: true);
			e.SuppressKeyPress = true;
		}
		else
		{
			if (e.KeyCode != Keys.Space)
			{
				return;
			}
			List<F40SlotRow> list = GetHighlightedCalibrationRows().ToList();
			if (list.Count == 0 && _grid.CurrentRow?.DataBoundItem is F40SlotRow item)
			{
				list.Add(item);
			}
			if (list.Count != 0)
			{
				SetCalibrationRowsSelected(list, list.Any((F40SlotRow r) => !r.Selected));
				e.SuppressKeyPress = true;
			}
		}
	}

	private void ApplyCalibrationCheckboxBatch(int rowIndex)
	{
		_grid.EndEdit();
		if (rowIndex < 0 || rowIndex >= _grid.Rows.Count || !(_grid.Rows[rowIndex].DataBoundItem is F40SlotRow { Selected: var selected } f40SlotRow))
		{
			return;
		}
		if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _lastCalibrationCheckRowIndex >= 0)
		{
			int num = Math.Min(_lastCalibrationCheckRowIndex, rowIndex);
			int num2 = Math.Max(_lastCalibrationCheckRowIndex, rowIndex);
			SetCalibrationRowsSelected((from i in Enumerable.Range(num, num2 - num + 1)
				select _grid.Rows[i].DataBoundItem).OfType<F40SlotRow>(), selected);
		}
		else
		{
			List<F40SlotRow> list = GetHighlightedCalibrationRows().ToList();
			if (list.Count > 1 && list.Contains(f40SlotRow))
			{
				SetCalibrationRowsSelected(list, selected);
			}
			else
			{
				UpdateStatusLabels();
			}
		}
		_lastCalibrationCheckRowIndex = rowIndex;
	}

	private IEnumerable<F40SlotRow> GetHighlightedCalibrationRows()
	{
		return (from DataGridViewRow r in _grid.SelectedRows
			select r.DataBoundItem).OfType<F40SlotRow>();
	}

	private void SetCalibrationRowsSelected(IEnumerable<F40SlotRow> rows, bool selected)
	{
		_grid.EndEdit();
		foreach (F40SlotRow row in rows)
		{
			row.Selected = selected;
		}
		_grid.Refresh();
		UpdateStatusLabels();
	}

	private void FormatCalibrationGridCell(object? sender, DataGridViewCellFormattingEventArgs e)
	{
		if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count || !(_grid.Rows[e.RowIndex].DataBoundItem is F40SlotRow f40SlotRow))
		{
			return;
		}
		if (e.Value is double d)
		{
			string text = ((e.CellStyle?.Format == "0.###") ? "0.###" : "0.######");
			e.Value = (double.IsNaN(d) ? "" : d.ToString(text, CultureInfo.InvariantCulture));
			e.FormattingApplied = true;
		}
		string text2 = f40SlotRow.Status ?? "";
		Color? color = null;
		Color foreColor = Color.FromArgb(15, 23, 42);
		if (text2.Contains("不合格", StringComparison.OrdinalIgnoreCase) || text2.Contains("失败", StringComparison.OrdinalIgnoreCase) || text2.Contains("错误", StringComparison.OrdinalIgnoreCase) || text2.Contains("跳过", StringComparison.OrdinalIgnoreCase) || text2.Contains("异常", StringComparison.OrdinalIgnoreCase))
		{
			color = Color.FromArgb(254, 226, 226);
			foreColor = Color.FromArgb(153, 27, 27);
		}
		else if (text2.Contains("完成", StringComparison.OrdinalIgnoreCase) || text2.Contains("合格", StringComparison.OrdinalIgnoreCase) || text2.Contains("OK", StringComparison.OrdinalIgnoreCase))
		{
			color = Color.FromArgb(220, 252, 231);
			foreColor = Color.FromArgb(22, 101, 52);
		}
		else if (text2.Contains("待继续", StringComparison.OrdinalIgnoreCase) || text2.Contains("待", StringComparison.OrdinalIgnoreCase) || text2.Contains("准备", StringComparison.OrdinalIgnoreCase))
		{
			color = Color.FromArgb(224, 231, 255);
			foreColor = Color.FromArgb(55, 48, 163);
		}
		else if (text2.Contains("标定中", StringComparison.OrdinalIgnoreCase) || text2.Contains("采", StringComparison.OrdinalIgnoreCase) || text2.Contains("复测", StringComparison.OrdinalIgnoreCase) || text2.Contains("写", StringComparison.OrdinalIgnoreCase))
		{
			color = Color.FromArgb(254, 249, 195);
			foreColor = Color.FromArgb(113, 63, 18);
		}
		else if (f40SlotRow.TestResult != 1)
		{
			color = Color.FromArgb(255, 241, 242);
			foreColor = Color.FromArgb(159, 18, 57);
		}
		if (color.HasValue)
		{
			Color valueOrDefault = color.GetValueOrDefault();
			DataGridViewCellStyle cellStyle = e.CellStyle;
			if (cellStyle != null)
			{
				cellStyle.BackColor = valueOrDefault;
				cellStyle.ForeColor = foreColor;
				cellStyle.SelectionBackColor = ControlPaint.Dark(valueOrDefault);
				cellStyle.SelectionForeColor = Color.White;
			}
		}
	}

	private void WireEvents()
	{
		_browse.Click += delegate
		{
			using OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Filter = "CSV (*.csv)|*.csv|All (*.*)|*.*",
				FileName = _csvPath.Text
			};
			if (openFileDialog.ShowDialog(this) == DialogResult.OK)
			{
				_csvPath.Text = openFileDialog.FileName;
			}
		};
		_loadCsv.Click += delegate
		{
			LoadCsvSafe(_csvPath.Text);
		};
		_selectValid.Click += delegate
		{
			foreach (F40SlotRow row in _rows)
			{
				row.Selected = row.TestResult == 1;
			}
			_grid.Refresh();
			UpdateStatusLabels();
		};
		_selectDaq60.Click += delegate
		{
			foreach (F40SlotRow row2 in _rows)
			{
				row2.Selected = row2.TestResult == 1 && IsSlotCoveredByDaqConfig(row2.Slot);
			}
			_grid.Refresh();
			UpdateStatusLabels();
		};
		_selectStableF40Slots.Click += delegate
		{
			LoadStableF40CalibrationRowsFromCache();
		};
		_copyRawDataMap.Click += delegate
		{
			try
			{
				CopyRawDataBySlotMap();
			}
			catch (Exception ex)
			{
				Log("复用原始数据失败：" + ex.Message, important: true);
				MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		};
		_selectAll.Click += delegate
		{
			foreach (F40SlotRow row3 in _rows)
			{
				row3.Selected = true;
			}
			_grid.Refresh();
			UpdateStatusLabels();
		};
		_selectNone.Click += delegate
		{
			foreach (F40SlotRow row4 in _rows)
			{
				row4.Selected = false;
			}
			_grid.Refresh();
			UpdateStatusLabels();
		};
		_applyChannelMap.Click += delegate
		{
			ApplyChannelMap();
		};
		_applyChannelMapDevice.Click += delegate
		{
			ApplyChannelMap();
		};
		_fillChannelSequence.Click += delegate
		{
			FillChannelSequenceWithErrorDialog();
		};
		_fillChannelSequenceRun.Click += delegate
		{
			FillChannelSequenceWithErrorDialog();
		};
		_applyCalModel.Click += delegate
		{
			ApplyCalibrationModelPressure(writeLog: true);
		};
		_calSensorModel.SelectedIndexChanged += delegate
		{
			ApplyCalibrationModelPressure(writeLog: false);
		};
		_refreshCom.Click += delegate
		{
			RefreshPorts();
		};
		_refreshVisa.Click += delegate
		{
			RefreshVisaResources();
			Log("已刷新VISA/GPIB资源", important: true);
		};
		_openSerial.Click += delegate
		{
			ToggleSerial();
		};
		_useGpib.CheckedChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_compUseOven.CheckedChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_compStartSlot.ValueChanged += delegate
		{
			RefreshCompensationSlotGrid(showLog: false);
		};
		_compSlotCount.ValueChanged += delegate
		{
			RefreshCompensationSlotGrid(showLog: false);
		};
		_compStartSlot.Leave += delegate
		{
			RefreshCompensationSlotGrid(showLog: false);
		};
		_compSlotCount.Leave += delegate
		{
			RefreshCompensationSlotGrid(showLog: false);
		};
		_compStartSlot.KeyDown += delegate(object? _, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Return)
			{
				RefreshCompensationSlotGrid(showLog: true);
				e.SuppressKeyPress = true;
			}
		};
		_compSlotCount.KeyDown += delegate(object? _, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Return)
			{
				RefreshCompensationSlotGrid(showLog: true);
				e.SuppressKeyPress = true;
			}
		};
		_pressureAddr.TextChanged += delegate
		{
			SyncGpibComboFromAddress(_pressureAddr, _pressureGpibPort, _pressureGpibAddress);
			UpdateDeviceStatusPanel();
		};
		_dmmAddr.TextChanged += delegate
		{
			SyncGpibComboFromAddress(_dmmAddr, _dmmGpibPort, _dmmGpibAddress);
			UpdateDeviceStatusPanel();
		};
		_ovenCom.TextChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_ovenIp.TextChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_ovenPort.TextChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_ovenUnitId.TextChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_compOvenModel.TextChanged += delegate
		{
			UpdateDeviceStatusPanel();
		};
		_calcSelected.Click += delegate
		{
			CalculateSelected();
		};
		_writeSelected.Click += async delegate
		{
			await SafeRunAsync(WriteSelectedAsync);
		};
		_start.Click += async delegate
		{
			await SafeRunAsync(AutoCalibrateAsync);
		};
		_startSingleCal.Click += async delegate
		{
			await SafeRunAsync(SingleCalibrateAsync);
		};
		_stop.Click += delegate
		{
			StopCurrentRun();
		};
		_writePreCalConfig.Click += async delegate
		{
			await SafeRunAsync(WritePreCalibrationConfigAsync);
		};
		_manualSend.Click += async delegate
		{
			await SafeRunAsync(ManualSendAsync);
		};
		_quickPing.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(170, Array.Empty<byte>(), 4, ct);
			});
		};
		_quickReadRaw.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(2, new byte[1] { (byte)_manualSlot.Value }, 13, ct);
			});
		};
		_quickReadCal.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(18, new byte[1] { (byte)_manualSlot.Value }, 13, ct);
			});
		};
		_quickEnterOwi.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(99, new byte[1] { (byte)_manualSlot.Value }, 5, ct);
			});
		};
		_quickExitOwi.Click += async delegate
		{
			await SafeRunAsync(async delegate(CancellationToken ct)
			{
				await QuickBoardAsync(97, new byte[1] { (byte)_manualSlot.Value }, 5, ct);
			});
		};
		_openLogDir.Click += delegate
		{
			OpenLogDirectory();
		};
		_saveConfig.Click += delegate
		{
			SaveAppConfig();
		};
		_checkUpdate.Click += async delegate
		{
			await CheckForUpdatesAsync();
		};
		_reloadConfig.Click += delegate
		{
			LoadAppConfig();
			LoadCommandFile();
			EnsureOriginalTcpOvenCommands();
			ApplyCalibrationTargetsToRows(resetDesiredPercents: true);
			ApplyChannelMap();
			Log("配置已重载", important: true);
		};
		_importIniCommand.Click += delegate
		{
			ImportIniCommand();
		};
		_loadCommandModel.Click += delegate
		{
			LoadCommandModelToGrid();
		};
		_commandModel.SelectedIndexChanged += delegate
		{
			LoadCommandModelToGrid();
		};
		_boardSlotMap.TextChanged += delegate
		{
			if (_boardSlotMapDevice.Text != _boardSlotMap.Text)
			{
				_boardSlotMapDevice.Text = _boardSlotMap.Text;
			}
			if (_boardSlotMapCal.Text != _boardSlotMap.Text)
			{
				_boardSlotMapCal.Text = _boardSlotMap.Text;
			}
		};
		_boardSlotMapDevice.TextChanged += delegate
		{
			if (_boardSlotMap.Text != _boardSlotMapDevice.Text)
			{
				_boardSlotMap.Text = _boardSlotMapDevice.Text;
			}
		};
		_boardSlotMapCal.TextChanged += delegate
		{
			if (_boardSlotMap.Text != _boardSlotMapCal.Text)
			{
				_boardSlotMap.Text = _boardSlotMapCal.Text;
			}
		};
		_useBoardChannel47.CheckedChanged += delegate
		{
			if (_useBoardChannel47Manual.Checked != _useBoardChannel47.Checked)
			{
				_useBoardChannel47Manual.Checked = _useBoardChannel47.Checked;
			}
		};
		_useBoardChannel47Manual.CheckedChanged += delegate
		{
			if (_useBoardChannel47.Checked != _useBoardChannel47Manual.Checked)
			{
				_useBoardChannel47.Checked = _useBoardChannel47Manual.Checked;
			}
		};
		_addr.ValueChanged += delegate
		{
			if (_syncBoardAddrBusy)
			{
				return;
			}
			_syncBoardAddrBusy = true;
			try
			{
				_preCalBoardAddr.Value = _addr.Value;
			}
			finally
			{
				_syncBoardAddrBusy = false;
			}
		};
		_preCalBoardAddr.ValueChanged += delegate
		{
			if (_syncBoardAddrBusy)
			{
				return;
			}
			_syncBoardAddrBusy = true;
			try
			{
				_addr.Value = _preCalBoardAddr.Value;
			}
			finally
			{
				_syncBoardAddrBusy = false;
			}
		};
		_pressureGpibAddress.SelectedIndexChanged += delegate
		{
			UpdateAddressesFromGpibNumeric();
		};
		_pressureGpibPort.SelectedIndexChanged += delegate
		{
			UpdateAddressesFromGpibNumeric();
		};
		_dmmGpibAddress.SelectedIndexChanged += delegate
		{
			UpdateAddressesFromGpibNumeric();
		};
		_dmmGpibPort.SelectedIndexChanged += delegate
		{
			UpdateAddressesFromGpibNumeric();
		};
		_pressureModel.TextChanged += delegate
		{
			if (_commands.ContainsKey(_pressureModel.Text))
			{
				_commandModel.Text = _pressureModel.Text;
				LoadCommandModelToGrid();
			}
			SyncDeviceGridFromControls();
		};
		_dmmModel.TextChanged += delegate
		{
			SyncDeviceGridFromControls();
		};
		_daqProfileGrid.DataError += delegate(object? _, DataGridViewDataErrorEventArgs e)
		{
			e.ThrowException = false;
		};
		_rows.ListChanged += delegate
		{
			UpdateStatusLabels();
		};
	}

	private async Task ManualSendAsync(CancellationToken ct)
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口");
		}
		byte slot = (byte)_manualSlot.Value;
		string selected = _manualFunction.Text;
		int expected = (int)_manualExpectedLen.Value;
		byte fn;
		byte[] payload;
		if (selected.StartsWith("AA"))
		{
			fn = 170;
			payload = Array.Empty<byte>();
			expected = 4;
		}
		else if (selected.StartsWith("02"))
		{
			fn = 2;
			payload = new byte[1] { slot };
			expected = 13;
		}
		else if (selected.StartsWith("12"))
		{
			fn = 18;
			payload = new byte[1] { slot };
			expected = 13;
		}
		else if (selected.StartsWith("63"))
		{
			fn = 99;
			payload = new byte[1] { slot };
			expected = 5;
		}
		else if (selected.StartsWith("61"))
		{
			fn = 97;
			payload = new byte[1] { slot };
			expected = 5;
		}
		else if (selected.StartsWith("76"))
		{
			fn = 118;
			payload = new byte[1] { slot };
			expected = 6;
		}
		else
		{
			if (selected.StartsWith("11"))
			{
				F40SlotRow row = _rows.FirstOrDefault((F40SlotRow x) => x.Slot == (int)_manualSlot.Value) ?? throw new InvalidOperationException("当前Slot不在CSV表格中");
				if (row.Coefficients.Length != 10)
				{
					row.CalculateCoefficients(preserveTempCoefficients: true);
				}
				row.EnsureCoefficientsValid();
				await _board.WriteCoefficientsAsync(slot, row.Coefficients, ct);
				Log($"手动写当前行系数完成：Slot{slot}", important: true);
				return;
			}
			byte[] bytes = ParseHex(_manualPayload.Text);
			if (bytes.Length == 0)
			{
				throw new InvalidOperationException("自定义功能码请在 Payload 中输入：功能码 数据...，例如 02 01");
			}
			fn = bytes[0];
			payload = bytes.Skip(1).ToArray();
		}
		await QuickBoardAsync(fn, payload, expected, ct);
	}

	private async Task QuickBoardAsync(byte fn, byte[] payload, int expectedLen, CancellationToken ct)
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口");
		}
		byte[] rsp = await _board.RequestAsync(fn, payload, expectedLen, ct);
		string extra = "";
		switch (fn)
		{
		case 2:
			try
			{
				(int BridgeRaw, int TempRaw) raw = ParseRaw02Response(rsp);
				extra = $" => 原始P={raw.BridgeRaw} T={raw.TempRaw}";
			}
			catch
			{
			}
			break;
		case 18:
			try
			{
				(double PressurePercent, double TempDeg, bool Valid) cal = ParseCalibrated12Response(rsp);
				extra = $" => 百分比P={cal.PressurePercent:0.######}% T={cal.TempDeg:0.######}℃ {(cal.Valid ? "OK" : "INVALID")}";
			}
			catch
			{
			}
			break;
		}
		LogComp($"指令完成 FN=0x{fn:X2} RX={string.Join(" ", rsp.Select((byte b) => b.ToString("X2")))}{extra}");
	}

	private static byte[] ParseHex(string text)
	{
		MatchCollection source = Regex.Matches(text, "[0-9A-Fa-f]{1,2}");
		return source.Select((Match m) => Convert.ToByte(m.Value, 16)).ToArray();
	}

	private static byte[] ParseConfigRegisterPair(string regA, string regB)
	{
		byte[] first = ParseRegisterWord(regA, "写入值A");
		byte[] second = ParseRegisterWord(regB, "写入值B");
		return first.Concat(second).ToArray();
	}

	private static byte[] ParseRegisterWord(string text, string name)
	{
		byte[] array = ParseHex(text);
		if (array.Length != 2)
		{
			throw new InvalidOperationException(name + " 必须是2字节/4位HEX，例如 0001 或 0267");
		}
		return array;
	}

	private static string NormalizeConfigGroup(string text)
	{
		text = text.Trim().Replace("/", "").Replace(" ", "");
		if (text == "0304" || text == "1415")
		{
			return text;
		}
		throw new InvalidOperationException("寄存器组合只能是 0304 或 1415");
	}

	private static string Hex(IEnumerable<byte> data)
	{
		return string.Join(" ", data.Select((byte b) => b.ToString("X2")));
	}

	private static string ShortError(string text)
	{
		text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
		return (text.Length <= 80) ? text : (text.Substring(0, 80) + "...");
	}

	private static byte ParseByteFlexible(string text)
	{
		text = text.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			return Convert.ToByte(text2.Substring(2, text2.Length - 2), 16);
		}
		if (Regex.IsMatch(text, "^[0-9A-Fa-f]{2}$"))
		{
			return Convert.ToByte(text, 16);
		}
		return Convert.ToByte(text, 10);
	}

	private static byte ParseIicAddress(string text)
	{
		text = text.Trim();
		byte b = (Regex.IsMatch(text, "^[01]{1,7}$") ? Convert.ToByte(text, 2) : ParseByteFlexible(text));
		if (b > 127)
		{
			throw new InvalidOperationException("IIC地址必须是7bit，范围 0..127");
		}
		return b;
	}

	private async Task CheckForUpdatesAsync()
	{
		if (_cts != null)
		{
			MessageBox.Show(this, "当前有生产任务正在运行，请任务结束后再检测更新。", "检测更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		if (!_checkUpdate.Enabled)
		{
			return;
		}
		string originalText = _checkUpdate.Text;
		_checkUpdate.Enabled = false;
		_checkUpdate.Text = "正在检查...";
		try
		{
			AppUpdateInfo update = await AppUpdateService.CheckAsync();
			if (update.Version <= AppUpdateService.CurrentVersion)
			{
				MessageBox.Show(this, $"当前已是最新版本 v{AppUpdateService.CurrentVersionText}。\r\n更新源：{update.ManifestSource}", "检测更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			string notes = string.IsNullOrWhiteSpace(update.Manifest.Notes) ? "无附加说明" : update.Manifest.Notes.Trim();
			DialogResult confirm = MessageBox.Show(this,
				$"发现新版本 v{update.Manifest.Version}，当前版本 v{AppUpdateService.CurrentVersionText}。\r\n\r\n{notes}\r\n\r\n更新优先从Gitee下载，失败后自动切换GitHub。安装时不会覆盖 setting、logs 和标定结果目录。\r\n\r\n是否立即下载并更新？",
				"发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
			if (confirm != DialogResult.Yes)
			{
				return;
			}
			Progress<int> progress = new Progress<int>(delegate(int value)
			{
				_checkUpdate.Text = $"下载 {value}%";
			});
			string packagePath = await AppUpdateService.DownloadAsync(update, progress);
			_checkUpdate.Text = "准备安装...";
			Log($"更新包下载并校验完成：v{update.Manifest.Version}，来源优先Gitee，文件={packagePath}", important: true);
			AppUpdateService.LaunchInstaller(packagePath, AppContext.BaseDirectory);
			Application.Exit();
		}
		catch (Exception ex)
		{
			Log("检测更新失败：" + ex.Message, important: true);
			MessageBox.Show(this, ex.Message, "检测更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
		finally
		{
			_checkUpdate.Text = originalText;
			_checkUpdate.Enabled = true;
		}
	}

	private void OpenLogDirectory()
	{
		string text = (string.IsNullOrWhiteSpace(_logFile) ? Path.Combine(AppContext.BaseDirectory, "logs") : Path.GetDirectoryName(_logFile));
		Directory.CreateDirectory(text);
		Process.Start(new ProcessStartInfo("explorer.exe", text)
		{
			UseShellExecute = true
		});
	}

	private void UpdateStatusLabels()
	{
		ToolStripStatusLabel statusSerial = _statusSerial;
		SerialBoardClient? board = _board;
		statusSerial.Text = ((board != null && board.IsOpen) ? ("板卡：已连接 " + _com.Text) : "板卡：未连接");
		_statusCsv.Text = $"CSV：{_rows.Count}行 / 有效{_rows.Count((F40SlotRow x) => x.TestResult == 1)}";
		_statusSelected.Text = $"选中：{_rows.Count((F40SlotRow x) => x.Selected)}";
		UpdateCalibrationOverview();
	}

	private void UpdateCalibrationOverview(string? stage = null)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				UpdateCalibrationOverview(stage);
			});
			return;
		}
		int selected = _rows.Count((F40SlotRow x) => x.Selected);
		int completed = _rows.Count((F40SlotRow x) => x.Selected && (x.Status.Contains("完成", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("合格", StringComparison.OrdinalIgnoreCase)));
		int failed = _rows.Count((F40SlotRow x) => x.Selected && (x.Status.Contains("不合格", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("失败", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("跳过", StringComparison.OrdinalIgnoreCase)));
		if (_calRunStateLabel != null && !_calRunStateLabel.IsDisposed)
		{
			bool running = _cts != null;
			_calRunStateLabel.Text = running ? "自动运行" : "待机";
			_calRunStateLabel.BackColor = running ? IndustrialSuccess : Color.FromArgb(221, 229, 233);
			_calRunStateLabel.ForeColor = running ? Color.White : IndustrialText;
		}
		if (_calProgressLabel != null && !_calProgressLabel.IsDisposed)
		{
			_calProgressLabel.Text = $"选中 {selected}  完成 {completed}  异常 {failed}";
			_calProgressLabel.BackColor = failed > 0 ? Color.FromArgb(248, 224, 221) : IndustrialSurfaceAlt;
			_calProgressLabel.ForeColor = failed > 0 ? IndustrialDanger : IndustrialText;
		}
		if (_calRecipeSummaryLabel != null && !_calRecipeSummaryLabel.IsDisposed)
		{
			_calRecipeSummaryLabel.Text = $"目标 {_calOutputMinV.Value:0.###} / {_calOutputMaxV.Value:0.###} V   容差 ±{_calVoltageTolerance.Value:0.###} V\r\n" + $"流程：{(_batchPressureMode.Checked ? "批量稳压" : "原版逐工位")}   写后复测：{(_verifyAfterWrite.Checked ? "启用" : "关闭")}";
		}
		if (_calInterlockLabel != null && !_calInterlockLabel.IsDisposed)
		{
			bool csvReady = _rows.Count > 0;
			bool boardReady = (!_writeBoard.Checked && !_writeConfigBeforeCal.Checked) || (_board?.IsOpen ?? false);
			bool pressureReady = !_useGpib.Checked || !string.IsNullOrWhiteSpace(_pressureAddr.Text);
			bool daqReady = !_useGpib.Checked || !string.IsNullOrWhiteSpace(_dmmAddr.Text);
			bool ready = csvReady && boardReady && pressureReady && daqReady && selected > 0;
			_calInterlockLabel.Text = (ready ? "互锁条件已满足" : "启动互锁未满足") + $"\r\nCSV {(csvReady ? "就绪" : "缺失")}  板卡 {(boardReady ? "就绪" : "未连接")}  压力 {(pressureReady ? "就绪" : "缺失")}  DAQ {(daqReady ? "就绪" : "缺失")}";
			_calInterlockLabel.BackColor = ready ? Color.FromArgb(219, 239, 228) : Color.FromArgb(245, 238, 216);
			_calInterlockLabel.ForeColor = ready ? Color.FromArgb(20, 91, 58) : Color.FromArgb(97, 67, 10);
		}
		if (stage != null && _calStageLabel != null && !_calStageLabel.IsDisposed)
		{
			string value = stage.Replace("\r", " ").Replace("\n", " ").Trim();
			_calStageLabel.Text = value.Length <= 96 ? value : value.Substring(0, 96) + "...";
			bool alarm = value.Contains("失败", StringComparison.OrdinalIgnoreCase) || value.Contains("异常", StringComparison.OrdinalIgnoreCase) || value.Contains("不合格", StringComparison.OrdinalIgnoreCase);
			_calStageLabel.ForeColor = alarm ? IndustrialDanger : IndustrialText;
		}
	}

	private void InitLog()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "logs");
		Directory.CreateDirectory(text);
		_logFile = Path.Combine(text, $"F40标定_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		Log("程序启动，日志：" + _logFile, important: true);
		Log("说明：板卡协议已按逆向结果实现：01 63 Slot -> 01 11 Slot + 10个Int32小端 -> 01 61 Slot。", important: true);
		Log("DLL为32位，因此本程序发布为win-x86自带.NET。", important: true);
	}

	private void RefreshPorts()
	{
		string text = NormalizePortName(_com.Text);
		string text2 = NormalizePortName(_ovenCom.Text);
		_com.Items.Clear();
		_ovenCom.Items.Clear();
		List<string> detectedPorts = GetDetectedSerialPorts();
		List<string> list = new List<string>(detectedPorts);
		foreach (string text3 in new string[2] { text, text2 })
		{
			if (!string.IsNullOrWhiteSpace(text3) && !list.Contains<string>(text3, StringComparer.OrdinalIgnoreCase))
			{
				list.Add(text3);
			}
		}
		if (list.Count == 0)
		{
			list.AddRange(from value in Enumerable.Range(1, 16)
				select $"COM{value}");
		}
		_com.Items.AddRange(list.Cast<object>().ToArray());
		_ovenCom.Items.AddRange(list.Cast<object>().ToArray());
		if (!string.IsNullOrWhiteSpace(text) && detectedPorts.Contains<string>(text, StringComparer.OrdinalIgnoreCase))
		{
			_com.Text = text;
		}
		else if (detectedPorts.Count > 0)
		{
			_com.Text = detectedPorts[0];
			if (!string.IsNullOrWhiteSpace(text))
			{
				Log($"配置里的板卡串口 {text} 当前未检测到，已切换到 {detectedPorts[0]}。", important: true);
			}
		}
		else if (!string.IsNullOrWhiteSpace(text))
		{
			_com.Text = text;
			Log($"配置里的板卡串口 {text} 当前未检测到，且系统未发现可用串口。", important: true);
		}
		else if (list.Count > 0)
		{
			_com.Text = list[0];
		}
		if (!string.IsNullOrWhiteSpace(text2) && detectedPorts.Contains<string>(text2, StringComparer.OrdinalIgnoreCase))
		{
			_ovenCom.Text = text2;
		}
		else if (detectedPorts.Count > 0)
		{
			_ovenCom.Text = detectedPorts[0];
			if (!string.IsNullOrWhiteSpace(text2))
			{
				Log($"配置里的烘箱串口 {text2} 当前未检测到，已切换到 {detectedPorts[0]}。", important: true);
			}
		}
		else if (!string.IsNullOrWhiteSpace(text2))
		{
			_ovenCom.Text = text2;
		}
		else if (list.Count > 0 && string.IsNullOrWhiteSpace(_ovenCom.Text))
		{
			_ovenCom.Text = list[0];
		}
		string text4 = detectedPorts.Count > 0 ? FormatAvailablePorts(detectedPorts) : "未检测到真实串口";
		Log("已刷新串口：可用=" + text4 + "；下拉项=" + string.Join(", ", list), important: true);
	}

	private static List<string> GetDetectedSerialPorts()
	{
		List<string> list = new List<string>();
		try
		{
			list.AddRange(SerialPort.GetPortNames());
		}
		catch
		{
		}
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DEVICEMAP\\SERIALCOMM");
			if (registryKey != null)
			{
				string[] valueNames = registryKey.GetValueNames();
				foreach (string name in valueNames)
				{
					string text = Convert.ToString(registryKey.GetValue(name))?.Trim();
					if (!string.IsNullOrWhiteSpace(text))
					{
						list.Add(text);
					}
				}
			}
		}
		catch
		{
		}
		return SortPorts(list);
	}

	private static List<string> SortPorts(IEnumerable<string> ports)
	{
		return (from x in (from x in ports
				select NormalizePortName(x) into x
				where !string.IsNullOrWhiteSpace(x)
				select x).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			orderby ParseComNumber(x)
			select x).ThenBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string NormalizePortName(string portName)
	{
		return (portName ?? "").Trim().ToUpperInvariant();
	}

	private static string FormatAvailablePorts(IEnumerable<string> ports)
	{
		List<string> list = SortPorts(ports);
		return list.Count == 0 ? "无" : string.Join(", ", list);
	}

	private static int ParseComNumber(string portName)
	{
		Match match = Regex.Match(portName ?? "", "COM(\\d+)", RegexOptions.IgnoreCase);
		int result;
		return (match.Success && int.TryParse(match.Groups[1].Value, out result)) ? result : int.MaxValue;
	}

	private void ToggleSerial()
	{
		try
		{
			SerialBoardClient? board = _board;
			if (board != null && board.IsOpen)
			{
				_board.Dispose();
				_board = null;
				_openSerial.Text = "打开板卡";
				Log("板卡串口已关闭");
				UpdateStatusLabels();
				UpdateDeviceStatusPanel();
				return;
			}
			if (string.IsNullOrWhiteSpace(_com.Text))
			{
				MessageBox.Show("没有选择COM");
				return;
			}
			string text = NormalizePortName(_com.Text);
			List<string> detectedSerialPorts = GetDetectedSerialPorts();
			if (detectedSerialPorts.Count > 0 && !detectedSerialPorts.Contains<string>(text, StringComparer.OrdinalIgnoreCase))
			{
				string text2 = $"当前板卡串口 {text} 未检测到。可用串口：{FormatAvailablePorts(detectedSerialPorts)}。请点“刷新串口”或选择正确COM后再打开。";
				Log("打开串口失败：" + text2, important: true);
				MessageBox.Show(text2, "板卡串口不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				RefreshPorts();
				UpdateDeviceStatusPanel();
				return;
			}
			if (detectedSerialPorts.Count == 0)
			{
				Log("打开串口前未检测到任何真实COM，仍按当前填写端口尝试打开：" + text, important: true);
			}
			int result;
			int baud = (int.TryParse(_boardBaud.Text, out result) ? result : 9600);
			int result2;
			int dataBits = (int.TryParse(_boardDataBits.Text, out result2) ? result2 : 8);
			Parity result3;
			Parity parity = (Enum.TryParse<Parity>(_boardParity.Text, ignoreCase: true, out result3) ? result3 : Parity.None);
			StopBits stopBits = ((!(_boardStopBits.Text == "2")) ? StopBits.One : StopBits.Two);
			_com.Text = text;
			_board = new SerialBoardClient(text, baud, dataBits, parity, stopBits, (byte)_addr.Value, (int)_timeout.Value, Log);
			_board.Open();
			_openSerial.Text = "关闭板卡";
			Log($"板卡串口打开：{text} {_boardBaud.Text},{_boardDataBits.Text},{_boardParity.Text},{_boardStopBits.Text} 站号={_addr.Value}", important: true);
			UpdateStatusLabels();
			UpdateDeviceStatusPanel();
		}
		catch (Exception ex)
		{
			Log($"打开串口失败：端口={NormalizePortName(_com.Text)}；可用串口={FormatAvailablePorts(GetDetectedSerialPorts())}；错误={ex.Message}", important: true);
			MessageBox.Show($"打开板卡串口失败：{ex.Message}\r\n\r\n当前端口：{NormalizePortName(_com.Text)}\r\n可用串口：{FormatAvailablePorts(GetDetectedSerialPorts())}\r\n\r\n请确认板卡USB/串口线已连接，设备管理器里能看到对应COM号。", "板卡串口打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			UpdateDeviceStatusPanel();
		}
	}

	private void ScrollGridToTop()
	{
		try
		{
			_grid.ClearSelection();
			if (_grid.Rows.Count > 0)
			{
				_grid.FirstDisplayedScrollingRowIndex = 0;
				_grid.CurrentCell = _grid.Rows[0].Cells[0];
			}
		}
		catch
		{
		}
	}

	private void ApplyChannelMap()
	{
		SyncDaqProfilesTextFromGrid();
		List<DaqProfile> daqProfiles = GetDaqProfiles();
		int profileIndex = 0;
		int? previousChannel = null;
		foreach (F40SlotRow row in _rows.OrderBy((F40SlotRow r) => r.Slot))
		{
			row.Channel = EvalChannel(row.Slot);
			row.DmmAddress = ResolveDmmAddressForSequentialRows(row.Slot, row.Channel, daqProfiles, ref profileIndex, ref previousChannel);
		}
		_grid.Refresh();
		string manual = string.IsNullOrWhiteSpace(_daqChannelOverrideMap.Text) ? "无手动覆盖" : ("手动覆盖=" + _daqChannelOverrideMap.Text.Trim());
		Log($"已应用DMM/DAQ通道映射：{_channelExpr.Text}；{manual}；{(_daqSkipChannel47.Checked ? "DAQ跳过4/7通道" : "DAQ不跳过4/7通道")}。示例 Slot1={EvalChannel(1)}, Slot9={EvalChannel(9)}, Slot17={EvalChannel(17)}, Slot33={EvalChannel(33)}, Slot41={EvalChannel(41)}, Slot57={EvalChannel(57)}", important: true);
	}

	private string ResolveDmmAddressForSequentialRows(int slot, string channelText, IReadOnlyList<DaqProfile> profiles, ref int profileIndex, ref int? previousChannel)
	{
		if (!_useDaqChannel.Checked)
		{
			return _dmmAddr.Text.Trim();
		}
		if (profiles.Count == 0)
		{
			return EvalDmmAddress(slot);
		}
		if (TryParseChannelNumber(channelText, out int currentChannel) && previousChannel.HasValue && currentChannel < previousChannel.Value && profileIndex < profiles.Count - 1)
		{
			profileIndex++;
		}
		if (TryParseChannelNumber(channelText, out int currentChannel2))
		{
			previousChannel = currentChannel2;
		}
		profileIndex = Math.Clamp(profileIndex, 0, profiles.Count - 1);
		return profiles[profileIndex].Address;
	}

	private void FillChannelSequenceWithErrorDialog()
	{
		try
		{
			FillChannelSequenceFromCurrentTemplate();
		}
		catch (Exception ex)
		{
			Log("顺填通道失败：" + ex.Message, important: true);
			MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	private void FillChannelSequenceFromCurrentTemplate()
	{
		_grid.EndEdit();
		List<F40SlotRow> list = _rows.OrderBy((F40SlotRow r) => r.Slot).ToList();
		if (list.Count == 0)
		{
			throw new InvalidOperationException("当前没有可填充的工位行。");
		}
		int startChannel = TryParseChannelNumber(list[0].Channel, out int value) ? value : -1;
		using InputBox inputBox = new InputBox("顺序填充通道起始值", startChannel > 0 ? startChannel.ToString(CultureInfo.InvariantCulture) : "213");
		if (inputBox.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		string text = inputBox.Value.Trim();
		if (!TryParseChannelNumber(text, out int seed))
		{
			throw new FormatException("起始通道必须是数字，例如 213。");
		}
		List<string> entries = new List<string>(list.Count);
		int current = seed;
		foreach (F40SlotRow row in list)
		{
			entries.Add($"{row.Slot}={current}");
			row.Channel = current.ToString(CultureInfo.InvariantCulture);
			current = NextDaqChannel(current);
		}
		_daqChannelOverrideMap.Text = string.Join(";", entries);
		_grid.Refresh();
		ApplyChannelMap();
		Log($"已顺序填充{list.Count}个工位的通道：起始={seed}，覆盖={_daqChannelOverrideMap.Text}", important: true);
	}

	private static bool TryParseChannelNumber(string? text, out int value)
	{
		return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	private static int NextDaqChannel(int channel)
	{
		if (channel >= 101 && channel < 120)
		{
			return channel + 1;
		}
		if (channel == 120)
		{
			return 201;
		}
		if (channel >= 201 && channel < 220)
		{
			return channel + 1;
		}
		if (channel == 220)
		{
			return 301;
		}
		if (channel >= 301 && channel < 320)
		{
			return channel + 1;
		}
		if (channel == 320)
		{
			return 101;
		}
		return channel + 1;
	}

	private static int[] StableF40CalibrationSlots()
	{
		return Enumerable.Range(1, 8)
			.Concat(Enumerable.Range(9, 8))
			.Concat(Enumerable.Range(17, 8))
			.Concat(Enumerable.Range(33, 8))
			.ToArray();
	}

	private void LoadStableF40CalibrationRowsFromCache()
	{
		if (_loadedCalibrationRows.Count == 0)
		{
			return;
		}
		Dictionary<int, F40SlotRow> bySlot = _loadedCalibrationRows
			.GroupBy((F40SlotRow x) => x.Slot)
			.ToDictionary((IGrouping<int, F40SlotRow> g) => g.Key, (IGrouping<int, F40SlotRow> g) => g.First());
		_rows.Clear();
		foreach (int slot in StableF40CalibrationSlots())
		{
			if (!bySlot.TryGetValue(slot, out F40SlotRow? source))
			{
				continue;
			}
			F40SlotRow item = CloneRowForTargetSlot(source, slot, source.Serial, source.TestResult, selected: true);
			item.DmmAddress = EvalDmmAddress(item.Slot);
			item.Channel = EvalChannel(item.Slot);
			_rows.Add(item);
		}
		ApplyCalibrationTargetsToRows(resetDesiredPercents: true);
		_grid.Refresh();
		UpdateStatusLabels();
		Log($"已固定加载4个可用通道32工位：{string.Join(",", StableF40CalibrationSlots().Take(8))} / 9-16 / 17-24 / 33-40；当前表格 {_rows.Count} 行。", important: true);
	}

	private static F40SlotRow CloneRowForTargetSlot(F40SlotRow source, int targetSlot, string serial, int testResult, bool selected)
	{
		return new F40SlotRow
		{
			Selected = selected,
			Slot = targetSlot,
			Serial = serial,
			TestResult = testResult,
			BridgeRaw = source.BridgeRaw.ToArray(),
			BridgeDesiredPercent = source.BridgeDesiredPercent.ToArray(),
			OriginalBridgeDesiredPercent = source.OriginalBridgeDesiredPercent.ToArray(),
			TempRaw = source.TempRaw.ToArray(),
			TempDesiredDeg = source.TempDesiredDeg.ToArray(),
			OriginalCoefficients = source.OriginalCoefficients.ToArray(),
			Coefficients = source.Coefficients.ToArray(),
			Status = "已复用原始数据"
		};
	}

	private void CopyRawDataBySlotMap()
	{
		if (_loadedCalibrationRows.Count == 0)
		{
			throw new InvalidOperationException("请先加载原始补偿CSV。");
		}
		using InputBox box = new InputBox("复用原始数据：源Slot=目标Slot，支持范围", "41-48=1-8;57-64=33-40");
		if (box.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		string text = box.Value.Trim();
		if (text.Length == 0)
		{
			return;
		}
		Dictionary<int, F40SlotRow> sourceBySlot = _loadedCalibrationRows
			.GroupBy((F40SlotRow x) => x.Slot)
			.ToDictionary((IGrouping<int, F40SlotRow> g) => g.Key, (IGrouping<int, F40SlotRow> g) => g.First());
		Dictionary<int, int> rowIndexBySlot = _rows
			.Select((F40SlotRow row, int index) => new { row, index })
			.ToDictionary(x => x.row.Slot, x => x.index);
		int copied = 0;
		foreach ((int sourceSlot, int targetSlot) in ParseSlotCopyMap(text))
		{
			if (!sourceBySlot.TryGetValue(sourceSlot, out F40SlotRow? source))
			{
				throw new InvalidOperationException($"源Slot{sourceSlot} 不在已加载CSV中。");
			}
			if (!rowIndexBySlot.TryGetValue(targetSlot, out int targetIndex))
			{
				throw new InvalidOperationException($"目标Slot{targetSlot} 不在当前32工位表格中。");
			}
			F40SlotRow oldTarget = _rows[targetIndex];
			F40SlotRow replacement = CloneRowForTargetSlot(source, oldTarget.Slot, source.Serial, source.TestResult, oldTarget.Selected);
			replacement.DmmAddress = oldTarget.DmmAddress;
			replacement.Channel = oldTarget.Channel;
			replacement.Status = $"原始数据<-Slot{sourceSlot}";
			_rows[targetIndex] = replacement;
			copied++;
		}
		ApplyCalibrationTargetsToRows(resetDesiredPercents: true);
		ApplyChannelMap();
		Log($"已复用原始补偿数据 {copied} 行：{text}", important: true);
	}

	private static List<(int SourceSlot, int TargetSlot)> ParseSlotCopyMap(string text)
	{
		List<(int SourceSlot, int TargetSlot)> result = new List<(int SourceSlot, int TargetSlot)>();
		string[] entries = text.Split(new char[4] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string entry in entries)
		{
			string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
			if (parts.Length != 2 || !TryParseIntRange(parts[0], out int sourceFrom, out int sourceTo) || !TryParseIntRange(parts[1], out int targetFrom, out int targetTo))
			{
				throw new FormatException("复用映射格式错误：" + entry + "，示例 41-48=1-8;57-64=33-40");
			}
			if (sourceFrom > sourceTo)
			{
				(sourceFrom, sourceTo) = (sourceTo, sourceFrom);
			}
			if (targetFrom > targetTo)
			{
				(targetFrom, targetTo) = (targetTo, targetFrom);
			}
			int sourceCount = sourceTo - sourceFrom;
			int targetCount = targetTo - targetFrom;
			if (sourceCount != targetCount)
			{
				throw new FormatException("复用映射数量不一致：" + entry);
			}
			for (int offset = 0; offset <= sourceCount; offset++)
			{
				result.Add((sourceFrom + offset, targetFrom + offset));
			}
		}
		return result;
	}

	private void LoadCsvSafe(string path)
	{
		try
		{
			InferCalibrationModelFromCsvPath(path);
			_loadedCalibrationRows = F40Csv.Load(path);
			LoadStableF40CalibrationRowsFromCache();
			Log($"已加载CSV：{path}，原始{_loadedCalibrationRows.Count}行；标定表固定4个可用通道{_rows.Count}行。可用“复用原始数据”把后续Slot数据填入当前32工位。", important: true);
			UpdateStatusLabels();
			ScrollGridToTop();
		}
		catch (Exception ex)
		{
			Log("加载CSV失败：" + ex.Message, important: true);
		}
	}

	private void CalculateSelected()
	{
		int num = 0;
		foreach (F40SlotRow item in _rows.Where((F40SlotRow x) => x.Selected))
		{
			try
			{
				item.CalculateCoefficients(preserveTempCoefficients: true);
				item.EnsureCoefficientsValid();
				item.Status = "已计算/系数验证通过";
				num++;
			}
			catch (Exception ex)
			{
				item.Status = "不合格/计算失败：" + ShortError(ex.Message);
			}
		}
		_grid.Refresh();
		Log($"只计算完成：{num}个工位", important: true);
	}

	private async Task SafeRunAsync(Func<CancellationToken, Task> action)
	{
		if (_cts != null)
		{
			return;
		}
		_cts = new CancellationTokenSource();
		SetRunning(running: true);
		try
		{
			await action(_cts.Token);
		}
		catch (OperationCanceledException)
		{
			Log("已停止", important: true);
		}
		catch (Exception ex2)
		{
			Log("运行异常：" + ex2, important: true);
		}
		finally
		{
			_cts.Dispose();
			_cts = null;
			SetRunning(running: false);
			_grid.Refresh();
		}
	}

	private void StopCurrentRun()
	{
		_cts?.Cancel();
		TryStopDevicesAfterCancel();
	}

	private void TryStopDevicesAfterCancel()
	{
		if (_compUseOven.Checked && HasOvenEndpoint())
		{
			try
			{
				using IOvenClient ovenClient = CreateOvenClient();
				ovenClient.Open();
				ovenClient.Write(CommandFor(_compOvenModel.Text, "Stop", "POWER,OFF"));
				Log("停止后已发送烘箱停止命令", important: true);
			}
			catch (Exception ex)
			{
				Log("停止后烘箱停机处理失败：" + ex.Message, important: true);
			}
		}
		if (!_useGpib.Checked || string.IsNullOrWhiteSpace(_pressureAddr.Text))
		{
			return;
		}
		try
		{
			using VisaInstrument visaInstrument = new VisaInstrument("STOP-PRESS", _pressureAddr.Text.Trim(), Log);
			visaInstrument.Open();
			visaInstrument.Write(CommandFor(_pressureModel.Text, "Vent", "*CLS;:Sour:Vent 1;:CAL:PRES:ZERP:VALV;*CLS;:OUTPUT OFF;*CLS;:OUTPUT ON"));
			Log("停止后已发送压力停机/泄压命令", important: true);
		}
		catch (Exception ex2)
		{
			Log("停止后设备停机处理失败：" + ex2.Message, important: true);
		}
	}

	private void SetRunning(bool running)
	{
		_start.Enabled = !running;
		_startSingleCal.Enabled = !running;
		_writeSelected.Enabled = !running;
		_calcSelected.Enabled = !running;
		_writePreCalConfig.Enabled = !running;
		_batchPressureMode.Enabled = !running;
		_stop.Enabled = running;
		_compStart.Enabled = !running;
		_compStop.Enabled = running;
		_testStart.Enabled = !running;
		_testStop.Enabled = running;
		UpdateDeviceStatusPanel();
		UpdateCalibrationOverview(running ? "任务已启动，正在执行设备互锁与流程准备" : "任务结束，系统已返回待机状态");
	}

	private async Task WriteSelectedAsync(CancellationToken ct)
	{
		if (!_writeBoard.Checked)
		{
			Log("未勾选写入板卡0x11，跳过写入");
			return;
		}
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口");
		}
		foreach (F40SlotRow r in _rows.Where((F40SlotRow x) => x.Selected))
		{
			ct.ThrowIfCancellationRequested();
			if (r.Coefficients.Length != 10)
			{
				r.CalculateCoefficients(preserveTempCoefficients: true);
			}
			r.EnsureCoefficientsValid();
			Log($"Slot{r.Slot} 写系数：{r.CoefficientsText}", important: true);
			BoardSlotTarget target = ResolveBoardSlot(r.Slot);
			await _board.WriteCoefficientsAsync(target.BoardAddr, target.LocalSlot, r.Coefficients, ct);
			Log($"GlobalSlot{r.Slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 写系数完成", important: true);
			r.WriteResult = "已写入";
			r.Status = "写入完成";
			_grid.Refresh();
		}
	}

	private async Task SingleCalibrateAsync(CancellationToken ct)
	{
		int slot = (int)_singleCalSlot.Value;
		foreach (F40SlotRow r in _rows)
		{
			r.Selected = r.Slot == slot;
		}
		F40SlotRow row = _rows.FirstOrDefault((F40SlotRow x) => x.Slot == slot) ?? throw new InvalidOperationException($"当前CSV中找不到 Slot{slot}。");
		Log($"开始单独标定：Slot{slot} {row.Serial}", important: true);
		await AutoCalibrateAsync(ct);
	}

	private async Task WritePreCalibrationConfigAsync(CancellationToken ct)
	{
		int startSlot = (int)_preCalStartSlot.Value;
		int count = (int)_preCalConfigCount.Value;
		List<int> targets = Enumerable.Range(startSlot, count).ToList();
		await WritePreCalibrationConfigAsync(targets, ct);
	}

	private async Task WritePreCalibrationConfigAsync(IReadOnlyList<int> targets, CancellationToken ct)
	{
		SerialBoardClient? board = _board;
		if (board == null || !board.IsOpen)
		{
			throw new InvalidOperationException("请先打开板卡串口");
		}
		if (targets.Count == 0)
		{
			throw new InvalidOperationException("没有需要写配置的工位。");
		}
		byte[] data = ParseConfigRegisterPair(_preCalRegAHex.Text, _preCalRegBHex.Text);
		string group = NormalizeConfigGroup(_preCalConfigGroup.Text);
		Log($"标定前写配置：目标工位={string.Join(",", targets.Take(20))}{((targets.Count > 20) ? "..." : "")} 寄存器组合={group} 写入值={_preCalRegAHex.Text.Trim()} / {_preCalRegBHex.Text.Trim()} 配置个数={targets.Count} 数据={Hex(data)}", important: true);
		foreach (int slot in targets)
		{
			ct.ThrowIfCancellationRequested();
			F40SlotRow row = _rows.FirstOrDefault((F40SlotRow x) => x.Slot == slot);
			if (row != null)
			{
				row.Status = "写" + group + "配置";
			}
			_grid.Refresh();
			BoardSlotTarget target = ResolveBoardSlot(slot);
			await _board.WriteConfigAsync(target.BoardAddr, target.LocalSlot, group, data, ct);
			Log($"GlobalSlot{slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 写{group}配置完成：{Hex(data)}", important: true);
		}
	}

	private void ValidateCalibrationInputs(IReadOnlyList<F40SlotRow> selected)
	{
		if (_rows.Count == 0)
		{
			throw new InvalidOperationException("请先加载原始补偿 CSV。");
		}
		if (selected.Count == 0)
		{
			throw new InvalidOperationException("没有选中工位。");
		}
		CalibrationL6.EnsureAvailable();
		GetBoardSlotRoutes();
		List<int> list = (from r in selected
			where r.TestResult != 1
			select r.Slot).Take(10).ToList();
		if (list.Count > 0)
		{
			Log("注意：选中了原补偿结果不是1的工位：" + string.Join(",", list), important: true);
		}
		if (Math.Abs(_pfull.Value - _p0.Value) < 0.000001m)
		{
			throw new InvalidOperationException("Pfull 不能等于 P0。");
		}
		if (!IsBetween((double)_pmid.Value, (double)_p0.Value, (double)_pfull.Value))
		{
			throw new InvalidOperationException("Pmid 必须位于 P0 和 Pfull 之间。");
		}
		if (_writeBoard.Checked || _writeConfigBeforeCal.Checked)
		{
			SerialBoardClient? board = _board;
			if (board == null || !board.IsOpen)
			{
				throw new InvalidOperationException("勾选了写入板卡/标定前写配置，请先打开板卡串口。");
			}
		}
		if (_writeConfigBeforeCal.Checked)
		{
			ParseConfigRegisterPair(_preCalRegAHex.Text, _preCalRegBHex.Text);
			NormalizeConfigGroup(_preCalConfigGroup.Text);
		}
		if (_useGpib.Checked && string.IsNullOrWhiteSpace(_pressureAddr.Text))
		{
			throw new InvalidOperationException("已启用 GPIB，但压力控制器 VISA 地址为空。");
		}
		if (_useGpib.Checked && !_useDaqChannel.Checked && string.IsNullOrWhiteSpace(_dmmAddr.Text))
		{
			throw new InvalidOperationException("已启用 GPIB 且未使用DAQ通道，但 DMM/DAQ VISA 地址为空。");
		}
		if (_useGpib.Checked && _useDaqChannel.Checked)
		{
			List<int> list2 = (from r in selected
				where string.IsNullOrWhiteSpace(r.DmmAddress) || string.IsNullOrWhiteSpace(r.Channel)
				select r.Slot).Take(10).ToList();
			if (list2.Count > 0)
			{
				throw new InvalidOperationException("以下工位未映射DAQ地址或通道：" + string.Join(",", list2) + "。请检查多DAQ配置或点“应用通道”。");
			}
		}
		if (_useGpib.Checked && _useDaqChannel.Checked && _useBoardChannel47.Checked && _daqSkipChannel47.Checked && string.IsNullOrWhiteSpace(_daqChannelOverrideMap.Text))
		{
			List<int> list3 = (from r in selected
				where r.Slot > 24
				select r.Slot).Take(10).ToList();
			if (list3.Count > 0)
			{
				throw new InvalidOperationException("当前板卡勾选“使用4/7通道”，但DAQ勾选“跳过4/7通道”，Slot25以后会出现采集通道与写入LocalSlot错位，已阻止本次标定。请先统一两边4/7策略，再运行这些工位。受影响工位：" + string.Join(",", list3));
			}
		}
		else if (_useGpib.Checked && _useDaqChannel.Checked && _useBoardChannel47.Checked && _daqSkipChannel47.Checked)
		{
			Log("已填写手动采集通道覆盖，允许板卡/DAQ 4/7策略不一致；请确认覆盖表已把101-120/201-220/301-320映射到实际标定工位。", important: true);
		}
		List<int> list4 = (from r in selected
			where r.BridgeRaw.Length == 0 || r.TempRaw.Length == 0 || r.BridgeDesiredPercent.Length == 0
			select r.Slot).Take(10).ToList();
		if (list4.Count > 0)
		{
			throw new InvalidOperationException("以下工位CSV原始数据不完整：" + string.Join(",", list4));
		}
	}

	private static string CalibrationRetryLabel(int maxRetries)
	{
		return (maxRetries <= 0) ? "不限" : maxRetries.ToString(CultureInfo.InvariantCulture);
	}

	private static bool IsVoltageInRange(double measured, double target, double tol)
	{
		return !double.IsNaN(measured) && Math.Abs(measured - target) <= tol;
	}

	private const int OriginalRecoveryMaxSteps = 11;

	private string BuildCalibrationFailReason(double lowV, double highV, double tol)
	{
		List<string> list = new List<string>();
		if (!IsVoltageInRange(lowV, CalibrationTargetMinV, tol))
		{
			list.Add($"低点{lowV:0.######}V");
		}
		if (!IsVoltageInRange(highV, CalibrationTargetMaxV, tol))
		{
			list.Add($"满点{highV:0.######}V");
		}
		return (list.Count == 0) ? "" : (string.Join(" / ", list) + $" 超差(±{tol:0.###}V)");
	}

	private async Task WriteAdjustedCoefficientsAsync(F40SlotRow row, string status, CancellationToken ct)
	{
		row.CalculateCoefficients(preserveTempCoefficients: true);
		row.EnsureCoefficientsValid();
		Log($"Slot{row.Slot} 修正百分比：低={row.NewMinPercent:0.######}% 中={row.NewMidPercent:0.######}% 满={row.NewMaxPercent:0.######}%");
		Log($"Slot{row.Slot} 新系数：{row.CoefficientsText}", important: true);
		if (_writeBoard.Checked)
		{
			row.Status = status;
			_grid.Refresh();
			BoardSlotTarget target = ResolveBoardSlot(row.Slot);
			await _board.WriteCoefficientsAsync(target.BoardAddr, target.LocalSlot, row.Coefficients, ct);
			row.WriteResult = "已写入";
			Log($"GlobalSlot{row.Slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 0x11写系数完成", important: true);
		}
		else
		{
			row.WriteResult = "未写入";
		}
	}

	private async Task InitializeCalibrationSlotAsync(F40SlotRow row, CancellationToken ct)
	{
		if (!_writeBoard.Checked)
		{
			return;
		}
		int[] initialCoefficients = row.OriginalCoefficients.Length == 10 ? row.OriginalCoefficients.ToArray() : row.Coefficients.ToArray();
		if (initialCoefficients.Length != 10)
		{
			throw new InvalidOperationException($"Slot{row.Slot} CSV中没有完整的10个初始系数，无法执行原程序的标定前初始化写入。");
		}
		int verifyResult = CalibrationL6.VerifyCoefficients(initialCoefficients);
		if (verifyResult != 0)
		{
			throw new InvalidOperationException($"Slot{row.Slot} CSV初始系数验证失败（VerifyCoefficients ret={verifyResult}），未向板卡写入。");
		}
		row.Status = "标定前初始化写入";
		_grid.Refresh();
		BoardSlotTarget target = ResolveBoardSlot(row.Slot);
		Log($"Slot{row.Slot} 按原程序执行标定前初始化：先写CSV现有10系数，再开始低点/满点测量。系数={string.Join(",", initialCoefficients)}", important: true);
		await _board.WriteCoefficientsAsync(target.BoardAddr, target.LocalSlot, initialCoefficients, ct);
		row.Coefficients = initialCoefficients;
		row.WriteResult = "初始化已写入";
		Log($"GlobalSlot{row.Slot} -> 板卡{target.BoardAddr} LocalSlot{target.LocalSlot} 标定前0x63->0x11->0x61初始化完成", important: true);
	}

	private async Task<double> RunOriginalZeroPhaseAsync(VisaInstrument? pressure, VisaInstrument? dmm, F40SlotRow row, int attempt, double lowV, double zeroCorrectionDelta, double fullCorrectionDelta, double tol, CancellationToken ct)
	{
		if (!_writeBoard.Checked)
		{
			return lowV;
		}
		for (int step = 1; step <= OriginalRecoveryMaxSteps; step++)
		{
			if (IsVoltageInRange(lowV, CalibrationTargetMinV, tol))
			{
				break;
			}
			row.Status = $"第{attempt}轮-零点修正{step}";
			_grid.Refresh();
			row.ApplyZeroRecoveryStep(zeroCorrectionDelta, fullCorrectionDelta);
			Log($"Slot{row.Slot} 原版零点重复修正：测量={lowV:0.######}V，重复首轮低点增量{zeroCorrectionDelta:+0.##;-0.##}%p、满点增量{fullCorrectionDelta:+0.##;-0.##}%p");
			await WriteAdjustedCoefficientsAsync(row, $"第{attempt}轮-写零点系数{step}", ct);
			row.Status = $"第{attempt}轮-零点复测{step}";
			_grid.Refresh();
			lowV = await MeasureAtPressureAsync(pressure, dmm, row, (double)_p0.Value, $"零点修正{step}", ct);
			row.ZeroV = lowV;
			_grid.Refresh();
		}
		return lowV;
	}

	private async Task<double> RunOriginalFullPhaseAsync(VisaInstrument? pressure, VisaInstrument? dmm, F40SlotRow row, int attempt, double highV, double fullCorrectionDelta, double tol, CancellationToken ct)
	{
		if (!_writeBoard.Checked)
		{
			return highV;
		}
		for (int step = 1; step <= OriginalRecoveryMaxSteps; step++)
		{
			if (IsVoltageInRange(highV, CalibrationTargetMaxV, tol))
			{
				break;
			}
			row.Status = $"第{attempt}轮-满点修正{step}";
			_grid.Refresh();
			row.ApplyFullRecoveryStep(fullCorrectionDelta);
			Log($"Slot{row.Slot} 原版满点重复修正：测量={highV:0.######}V，重复首轮满点增量{fullCorrectionDelta:+0.##;-0.##}%p");
			await WriteAdjustedCoefficientsAsync(row, $"第{attempt}轮-写满点系数{step}", ct);
			row.Status = $"第{attempt}轮-满点复测{step}";
			_grid.Refresh();
			highV = await MeasureAtPressureAsync(pressure, dmm, row, (double)_pfull.Value, $"满点修正{step}", ct);
			row.FullV = highV;
			_grid.Refresh();
		}
		return highV;
	}

	private void ResetCalibrationMeasurementsForRun(IEnumerable<F40SlotRow> rows)
	{
		foreach (F40SlotRow row in rows)
		{
			row.PreZeroV = double.NaN;
			row.PreFullV = double.NaN;
			row.ZeroV = double.NaN;
			row.FullV = double.NaN;
			row.PostMidV = double.NaN;
			row.LinearityPercent = double.NaN;
			row.WriteResult = "";
		}
		_grid.Refresh();
	}

	private string SaveCalibrationResultCsv(IReadOnlyList<F40SlotRow> rows)
	{
		string resultDir = Path.Combine(AppContext.BaseDirectory, "标定结果");
		Directory.CreateDirectory(resultDir);
		string model = SanitizeResultFileName(_calSensorModel.Text.Trim());
		if (string.IsNullOrWhiteSpace(model))
		{
			model = "F40";
		}
		string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
		string path = Path.Combine(resultDir, $"{model}_{timestamp}.csv");
		for (int suffix = 2; File.Exists(path); suffix++)
		{
			path = Path.Combine(resultDir, $"{model}_{timestamp}_{suffix}.csv");
		}

		StringBuilder sb = new StringBuilder();
		sb.AppendLine("Slot,序列号,原补偿,DMM/DAQ地址,DMM通道,写前低V,写前满V,写后低V,写后满V,写后中V,新低%,新中%,新满%,线性%,写系数,10系数,状态");
		foreach (F40SlotRow row in rows.OrderBy((F40SlotRow item) => item.Slot))
		{
			sb.Append(row.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(Csv(row.Serial)).Append(',')
				.Append(row.TestResult.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(Csv(row.DmmAddress)).Append(',')
				.Append(Csv(row.Channel)).Append(',')
				.Append(FormatCompCell(row.PreZeroV)).Append(',')
				.Append(FormatCompCell(row.PreFullV)).Append(',')
				.Append(FormatCompCell(row.ZeroV)).Append(',')
				.Append(FormatCompCell(row.FullV)).Append(',')
				.Append(FormatCompCell(row.PostMidV)).Append(',')
				.Append(FormatCompCell(row.NewMinPercent)).Append(',')
				.Append(FormatCompCell(row.NewMidPercent)).Append(',')
				.Append(FormatCompCell(row.NewMaxPercent)).Append(',')
				.Append(FormatCompCell(row.LinearityPercent)).Append(',')
				.Append(Csv(row.WriteResult)).Append(',')
				.Append(Csv(row.CoefficientsText)).Append(',')
				.Append(Csv(row.Status ?? ""))
				.AppendLine();
		}
		File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		Log($"标定结果已保存：{path}，共{rows.Count}行。", important: true);
		return path;
	}

	private static string SanitizeResultFileName(string value)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		return new string((value ?? "").Select((char c) => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
	}

	private async Task AutoCalibrateAsync(CancellationToken ct)
	{
		ApplyChannelMap();
		List<F40SlotRow> selected = _rows.Where((F40SlotRow x) => x.Selected).ToList();
		ValidateCalibrationInputs(selected);
		ResetCalibrationMeasurementsForRun(selected);
		await PrepareBoardForCalibrationAsync(selected, ct);
		if (_batchPressureMode.Checked)
		{
			Log("已启用批量稳压加速：每槽仍按原版两点修正算法独立计算，但压力调度为融合版批量节奏，不是原程序逐工位原样流程。", important: true);
			await AutoCalibrateBatchPressureAsync(ct);
			SaveCalibrationResultCsv(selected);
			return;
		}
		Dictionary<string, VisaInstrument> dmms;
		using (VisaInstrument pressure = (_useGpib.Checked ? new VisaInstrument("PRESS", _pressureAddr.Text, Log) : null))
		{
			dmms = new Dictionary<string, VisaInstrument>(StringComparer.OrdinalIgnoreCase);
			int okCount = 0;
			int failCount = 0;
			try
			{
				pressure?.Open();
				pressure?.Query(CommandFor(_pressureModel.Text, "MachineType", "*IDN?"));
				double tol = EffectiveAutoCalibrationToleranceV;
				int maxRetries = CalibrationMaxRetryCount;
				bool unlimitedRetries = maxRetries <= 0;
				string retryLabel = CalibrationRetryLabel(maxRetries);
				List<F40SlotRow> pending = new List<F40SlotRow>(selected);
				HashSet<int> passedSlots = new HashSet<int>();
				if (_writeConfigBeforeCal.Checked)
				{
					Log("已启用标定前写配置：自动标定时按工位逐个写入，单个工位失败会标红并跳过。", important: true);
				}
				Log($"逐工位标定已开始：共选中 {selected.Count} 个工位，最大轮次 {retryLabel}，首写采用两点联合修正并量化到两位；超差后重复首轮修正量，输出容差 ±{tol:0.###}V", important: true);
				int attempt = 1;
				while (pending.Count > 0 && (unlimitedRetries || attempt <= maxRetries))
				{
					ct.ThrowIfCancellationRequested();
					if (passedSlots.Count > 0)
					{
						pending = pending.Where((F40SlotRow f40SlotRow) => !passedSlots.Contains(f40SlotRow.Slot)).ToList();
						if (pending.Count == 0)
						{
							break;
						}
					}
					Log($"========== 第{attempt}/{retryLabel}轮逐工位标定开始：待标定 {pending.Count} 个 ==========", important: true);
					foreach (F40SlotRow r in pending)
					{
						r.Status = $"第{attempt}轮-准备";
					}
					_grid.Refresh();
					foreach (F40SlotRow r2 in pending.ToList())
					{
						ct.ThrowIfCancellationRequested();
						try
						{
							Log($"========== Slot{r2.Slot} {r2.Serial} 第{attempt}/{retryLabel}轮开始标定 DAQ={(_useDaqChannel.Checked ? r2.DmmAddress : _dmmAddr.Text.Trim())} CH={r2.Channel} ==========", important: true);
							LogSlotRoute(r2, $"Slot{r2.Slot}");
							if (_writeConfigBeforeCal.Checked)
							{
								r2.Status = $"第{attempt}轮-写前置配置";
								_grid.Refresh();
								await WritePreCalibrationConfigAsync(new[] { r2.Slot }, ct);
							}
							if (attempt == 1)
							{
								await InitializeCalibrationSlotAsync(r2, ct);
							}
							VisaInstrument dmm = GetDmm(r2);
							r2.Status = $"第{attempt}轮-采低点";
							_grid.Refresh();
							double lowV = (r2.PreZeroV = await MeasureAtPressureAsync(pressure, dmm, r2, (double)_p0.Value, "低点", ct));
							_grid.Refresh();
							r2.Status = $"第{attempt}轮-采满点";
							_grid.Refresh();
							double highV = (r2.PreFullV = await MeasureAtPressureAsync(pressure, dmm, r2, (double)_pfull.Value, "满点", ct));
							_grid.Refresh();
							if (TryCompleteCalibrationWithoutRewrite(r2, lowV, highV, tol, $"Slot{r2.Slot} 写前已达标"))
							{
								okCount++;
								passedSlots.Add(r2.Slot);
								pending.Remove(r2);
								_grid.Refresh();
								continue;
							}
							Log($"Slot{r2.Slot} 第{attempt}轮执行原版首写：由同一组低点/满点测量值联合修正低、中、满目标。", important: true);
							double originalLowV = lowV;
							double originalFullV = highV;
							double previousMinTarget = r2.NewMinPercent;
							double previousMaxTarget = r2.NewMaxPercent;
							r2.ApplyTwoPointOutputCorrection(originalLowV, originalFullV);
							double zeroCorrectionDelta = r2.NewMinPercent - previousMinTarget;
							double fullCorrectionDelta = r2.NewMaxPercent - previousMaxTarget;
							await WriteAdjustedCoefficientsAsync(r2, $"第{attempt}轮-两点联合首写", ct);
							r2.Status = $"第{attempt}轮-首写零点复测";
							_grid.Refresh();
							lowV = (r2.ZeroV = await MeasureAtPressureAsync(pressure, dmm, r2, (double)_p0.Value, "首写零点复测", ct));
							lowV = await RunOriginalZeroPhaseAsync(pressure, dmm, r2, attempt, lowV, zeroCorrectionDelta, fullCorrectionDelta, tol, ct);
							if (!IsVoltageInRange(lowV, CalibrationTargetMinV, tol))
							{
								failCount++;
								r2.Status = $"完成/不合格：零点{OriginalRecoveryMaxSteps}次未达标";
								r2.ZeroV = lowV;
								Log($"Slot{r2.Slot} 零点步进修正{OriginalRecoveryMaxSteps}次仍未达到{FormatVoltageTarget(CalibrationTargetMinV)}，跳过当前工位：{BuildCalibrationFailReason(lowV, highV, tol)}", important: true);
								pending.Remove(r2);
								_grid.Refresh();
								continue;
							}
							r2.Status = $"第{attempt}轮-满点起始";
							_grid.Refresh();
							highV = (r2.FullV = await MeasureAtPressureAsync(pressure, dmm, r2, (double)_pfull.Value, "满点起始", ct));
							_grid.Refresh();
							highV = await RunOriginalFullPhaseAsync(pressure, dmm, r2, attempt, highV, fullCorrectionDelta, tol, ct);
							if (!IsVoltageInRange(highV, CalibrationTargetMaxV, tol))
							{
								failCount++;
								r2.Status = $"完成/不合格：满点{OriginalRecoveryMaxSteps}次未达标";
								r2.ZeroV = lowV;
								r2.FullV = highV;
								Log($"Slot{r2.Slot} 满点步进修正{OriginalRecoveryMaxSteps}次仍未达到{FormatVoltageTarget(CalibrationTargetMaxV)}，跳过当前工位：{BuildCalibrationFailReason(lowV, highV, tol)}", important: true);
								pending.Remove(r2);
								_grid.Refresh();
								continue;
							}
							double verifyLow = lowV;
							double verifyHigh = highV;
							double? verifyMid = null;
							r2.ZeroV = verifyLow;
							r2.FullV = verifyHigh;
							WarnIfWriteDidNotMoveOutput(r2, verifyLow, verifyHigh);
							if (_verifyAfterWrite.Checked && CalibrationLinearityEnabled && IsVoltageInRange(verifyLow, CalibrationTargetMinV, tol) && IsVoltageInRange(verifyHigh, CalibrationTargetMaxV, tol))
							{
								r2.Status = $"第{attempt}轮-复测中点";
								_grid.Refresh();
								verifyMid = await MeasureAtPressureAsync(pressure, dmm, r2, (double)_pmid.Value, "复测中点", ct);
							}
							bool passed = IsVoltageInRange(verifyLow, CalibrationTargetMinV, tol) && IsVoltageInRange(verifyHigh, CalibrationTargetMaxV, tol);
							string finalReason = BuildCalibrationFailReason(verifyLow, verifyHigh, tol);
							if (passed)
							{
								if (verifyMid.HasValue)
								{
									r2.PostMidV = verifyMid.Value;
									r2.LinearityPercent = F40SlotRow.Linearity(verifyMid.Value, verifyLow, verifyHigh);
									Log($"Slot{r2.Slot} 复测：低={verifyLow:0.######}V 满={verifyHigh:0.######}V 中={verifyMid.Value:0.######}V 线性={r2.LinearityPercent:0.###}%", important: true);
								}
								else
								{
									r2.PostMidV = double.NaN;
									r2.LinearityPercent = double.NaN;
									Log($"Slot{r2.Slot} 复测：低={verifyLow:0.######}V 满={verifyHigh:0.######}V", important: true);
								}
								okCount++;
								passedSlots.Add(r2.Slot);
								r2.Status = "完成/合格";
								Log($"Slot{r2.Slot} 标定合格：低点={verifyLow:0.######}V 满点={verifyHigh:0.######}V", important: true);
								pending.Remove(r2);
							}
							else if (!_writeBoard.Checked)
							{
								failCount++;
								r2.Status = "完成/不合格：未写入板卡";
								Log($"Slot{r2.Slot} 未勾选写入板卡0x11，无法继续按原版连续修正：{finalReason}", important: true);
								pending.Remove(r2);
							}
							else if (!unlimitedRetries && attempt >= maxRetries)
							{
								failCount++;
								r2.Status = "完成/不合格：" + finalReason;
								Log($"Slot{r2.Slot} 达到最大复标次数仍不合格：{finalReason}", important: true);
								pending.Remove(r2);
							}
							else
							{
								r2.Status = $"第{attempt + 1}轮-待继续";
								Log($"Slot{r2.Slot} 第{attempt}轮后仍未达标，保留到下一轮：{finalReason}", important: true);
							}
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception ex2)
						{
							failCount++;
							r2.Status = "不合格/跳过：" + ShortError(ex2.Message);
							Log($"Slot{r2.Slot} 第{attempt}轮标定失败，已跳过：{ex2.Message}", important: true);
							pending.Remove(r2);
						}
						_grid.Refresh();
					}
					Log($"第{attempt}轮完成：本轮后剩余待标定 {pending.Count} 个，累计合格 {okCount} 个，不合格/跳过 {failCount} 个。", important: true);
					_grid.Refresh();
					attempt++;
				}
				Log($"全部选中工位标定流程完成：合格/完成 {okCount} 个，不合格/跳过 {failCount} 个。", important: true);
			}
			finally
			{
				foreach (VisaInstrument d in dmms.Values)
				{
					d.Dispose();
				}
			}
		}
		SaveCalibrationResultCsv(selected);
		VisaInstrument? GetDmm(F40SlotRow row)
		{
			if (!_useGpib.Checked)
			{
				return null;
			}
			string text = (_useDaqChannel.Checked ? row.DmmAddress : _dmmAddr.Text.Trim());
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new InvalidOperationException($"Slot{row.Slot} 未配置DAQ/DMM GPIB地址，请检查多DAQ配置。");
			}
			if (!dmms.TryGetValue(text, out VisaInstrument value))
			{
				value = new VisaInstrument("DMM/DAQ " + text, text, Log);
				value.Open();
				value.Query(CommandFor(_dmmModel.Text, "MachineType", "*IDN?"));
				dmms[text] = value;
			}
			return value;
		}
	}

	private async Task PrepareBoardForCalibrationAsync(IReadOnlyList<F40SlotRow> selected, CancellationToken ct)
	{
		if (!_writeBoard.Checked)
		{
			Log("未勾选写入板卡0x11：本次仅测量，不会执行实际标定写入。", important: true);
			return;
		}
		SerialBoardClient board = _board ?? throw new InvalidOperationException("请先打开板卡串口");
		List<byte> boardAddresses = selected.Select((F40SlotRow row) => ResolveBoardSlot(row.Slot).BoardAddr).Distinct().OrderBy((byte addr) => addr).ToList();
		foreach (byte boardAddress in boardAddresses)
		{
			ct.ThrowIfCancellationRequested();
			await board.EnsureNormalOutputModeAsync(boardAddress, ct);
		}
		Log($"板卡标定前检查完成：{string.Join(",", boardAddresses.Select((byte x) => "板卡" + x))} 均已响应并处于正常输出模式。随后才会测低点/满点、计算系数并执行0x63->0x11->0x61闭环。", important: true);
	}

	private void LogSlotRoute(F40SlotRow row, string prefix)
	{
		BoardSlotTarget boardSlotTarget = ResolveBoardSlot(row.Slot);
		string value = (_useDaqChannel.Checked ? row.DmmAddress : _dmmAddr.Text.Trim());
		Log($"{prefix} 路由：GlobalSlot{row.Slot} -> 板卡{boardSlotTarget.BoardAddr} LocalSlot{boardSlotTarget.LocalSlot}；DAQ={value} CH={row.Channel}；板卡{(_useBoardChannel47.Checked ? "使用" : "跳过")}4/7通道，DAQ{(_daqSkipChannel47.Checked ? "跳过" : "不跳过")}4/7通道", important: true);
		if (_useBoardChannel47.Checked && _daqSkipChannel47.Checked && row.Slot > 24)
		{
			Log($"注意：Slot{row.Slot} 当前板卡和DAQ的4/7通道策略不同，Slot25以后可能出现采集通道与写入本地槽错位。", important: true);
		}
	}

	private bool TryGetWriteOutputMovement(F40SlotRow row, double verifyLow, double verifyHigh, out double lowMove, out double highMove)
	{
		lowMove = double.NaN;
		highMove = double.NaN;
		if (double.IsNaN(row.PreZeroV) || double.IsNaN(row.PreFullV) || double.IsNaN(verifyLow) || double.IsNaN(verifyHigh))
		{
			return false;
		}
		lowMove = Math.Abs(verifyLow - row.PreZeroV);
		highMove = Math.Abs(verifyHigh - row.PreFullV);
		return true;
	}

	private bool WarnIfWriteDidNotMoveOutput(F40SlotRow row, double verifyLow, double verifyHigh)
	{
		if (!TryGetWriteOutputMovement(row, verifyLow, verifyHigh, out var lowMove, out var highMove))
		{
			return false;
		}
		if (lowMove < 0.01 && highMove < 0.01)
		{
			Log($"警告：Slot{row.Slot} 写系数后输出几乎没变化，低点变化={lowMove:0.######}V 满点变化={highMove:0.######}V。优先检查：该Slot是否已写0304/1415配置、板卡LocalSlot是否对应、0x11 ACK及写入链路是否正常。", important: true);
			return true;
		}
		return false;
	}

	private bool TryMarkWriteIneffectiveFailure(F40SlotRow row, double verifyLow, double verifyHigh, double tol)
	{
		if (!_writeBoard.Checked)
		{
			return false;
		}
		if (IsVoltageInRange(verifyLow, CalibrationTargetMinV, tol) && IsVoltageInRange(verifyHigh, CalibrationTargetMaxV, tol))
		{
			return false;
		}
		if (!TryGetWriteOutputMovement(row, verifyLow, verifyHigh, out var lowMove, out var highMove))
		{
			return false;
		}
		if (lowMove >= 0.01 || highMove >= 0.01)
		{
			return false;
		}
		string value = BuildCalibrationFailReason(verifyLow, verifyHigh, tol);
		row.Status = "完成/不合格：写入后输出几乎无变化";
		row.WriteResult = "已写入/疑似未生效";
		Log($"Slot{row.Slot} 写入后输出几乎无变化，判定本轮写入未生效，不再继续复标：低点变化={lowMove:0.######}V 满点变化={highMove:0.######}V；当前结果：{value}。请优先检查0304/1415配置、LocalSlot映射、0x11 ACK和板卡写入链路。", important: true);
		return true;
	}

	private bool TryCompleteCalibrationWithoutRewrite(F40SlotRow row, double lowV, double highV, double tol, string logPrefix)
	{
		if (!IsVoltageInRange(lowV, CalibrationTargetMinV, tol) || !IsVoltageInRange(highV, CalibrationTargetMaxV, tol))
		{
			return false;
		}
		row.ZeroV = lowV;
		row.FullV = highV;
		row.PostMidV = double.NaN;
		row.LinearityPercent = double.NaN;
		row.WriteResult = row.WriteResult == "初始化已写入" ? "初始化已写入/无需修正" : "写前已达标/未重写";
		row.Status = "完成/合格(写前已达标)";
		Log($"{logPrefix}：低点={lowV:0.######}V 满点={highV:0.######}V，已跳过重新计算和0x11写入。", important: true);
		return true;
	}

	private async Task AutoCalibrateBatchPressureAsync(CancellationToken ct)
	{
		await AutoCalibrateBatchPressureOriginalFlowAsync(ct);
	}

	private async Task AutoCalibrateBatchPressureOriginalFlowAsync(CancellationToken ct)
	{
		ApplyChannelMap();
		List<F40SlotRow> selected = _rows.Where((F40SlotRow x) => x.Selected).ToList();
		ValidateCalibrationInputs(selected);
		VisaInstrument pressure = (_useGpib.Checked ? new VisaInstrument("PRESS", _pressureAddr.Text, Log) : null);
		Dictionary<string, VisaInstrument> dmms;
		int failCount;
		HashSet<int> passedSlots;
		try
		{
			dmms = new Dictionary<string, VisaInstrument>(StringComparer.OrdinalIgnoreCase);
			int okCount = 0;
			failCount = 0;
			int maxRetries = CalibrationMaxRetryCount;
			bool unlimitedRetries = maxRetries <= 0;
			string retryLabel = CalibrationRetryLabel(maxRetries);
			double tol = EffectiveAutoCalibrationToleranceV;
			List<F40SlotRow> pending = new List<F40SlotRow>(selected);
			passedSlots = new HashSet<int>();
			try
			{
				pressure?.Open();
				if (pressure != null)
				{
					pressure.Query(CommandFor(_pressureModel.Text, "MachineType", "*IDN?"));
				}
				Log($"批量稳压标定已开始：共选中 {selected.Count} 个工位，最大轮次 {retryLabel}。每槽算法采用原版两位量化和重复首轮修正量；压力切换顺序属于融合版批量调度，输出容差 ±{tol:0.###}V", important: true);
				int attempt = 1;
				while (pending.Count > 0 && (unlimitedRetries || attempt <= maxRetries))
				{
					ct.ThrowIfCancellationRequested();
					pending = ActiveRows(pending);
					if (pending.Count == 0)
					{
						break;
					}
					foreach (F40SlotRow r in pending)
					{
						r.Status = $"第{attempt}轮-批量准备";
						r.WriteResult = "";
						r.PostMidV = double.NaN;
						r.LinearityPercent = double.NaN;
						LogSlotRoute(r, $"Slot{r.Slot}");
					}
					_grid.Refresh();
					Log($"========== 第{attempt}/{retryLabel}轮批量稳压标定开始：待标定 {pending.Count} 个 ==========", important: true);
					if (_writeConfigBeforeCal.Checked)
					{
						foreach (F40SlotRow r2 in pending.ToList())
						{
							try
							{
								r2.Status = $"第{attempt}轮-写前置配置";
								_grid.Refresh();
								await WritePreCalibrationConfigAsync(new[] { r2.Slot }, ct);
							}
							catch (Exception ex) when (!(ex is OperationCanceledException))
							{
								MarkFailed(r2, ex.Message);
								pending.Remove(r2);
							}
						}
					}
					if (attempt == 1 && _writeBoard.Checked)
					{
						foreach (F40SlotRow initializationRow in pending.ToList())
						{
							try
							{
								await InitializeCalibrationSlotAsync(initializationRow, ct);
							}
							catch (Exception ex) when (!(ex is OperationCanceledException))
							{
								MarkFailed(initializationRow, ex.Message);
								pending.Remove(initializationRow);
							}
						}
					}
					pending = ActiveRows(pending);
					await MeasureBatchAsync(pending, (double)_p0.Value, $"第{attempt}轮-低点批量", $"第{attempt}轮-写前低点", delegate(F40SlotRow f40SlotRow, double v)
					{
						f40SlotRow.PreZeroV = v;
					});
					pending = ActiveRows(pending);
					await MeasureBatchAsync(pending, (double)_pfull.Value, $"第{attempt}轮-满点批量", $"第{attempt}轮-写前满点", delegate(F40SlotRow f40SlotRow, double v)
					{
						f40SlotRow.PreFullV = v;
					});
					pending = ActiveRows(pending);
					foreach (F40SlotRow r3 in pending.ToList())
					{
						if (TryCompleteCalibrationWithoutRewrite(r3, r3.PreZeroV, r3.PreFullV, tol, $"Slot{r3.Slot} 写前已达标"))
						{
							okCount++;
							passedSlots.Add(r3.Slot);
							pending.Remove(r3);
						}
					}
					_grid.Refresh();
					Dictionary<int, double> zeroCorrectionDelta = new Dictionary<int, double>();
					Dictionary<int, double> fullCorrectionDelta = new Dictionary<int, double>();
					if (_writeBoard.Checked)
					{
						foreach (F40SlotRow firstWriteRow in ActiveRows(pending).ToList())
						{
							try
							{
								firstWriteRow.Status = $"第{attempt}轮-两点联合首写";
								double previousMinTarget = firstWriteRow.NewMinPercent;
								double previousMaxTarget = firstWriteRow.NewMaxPercent;
								firstWriteRow.ApplyTwoPointOutputCorrection(firstWriteRow.PreZeroV, firstWriteRow.PreFullV);
								zeroCorrectionDelta[firstWriteRow.Slot] = firstWriteRow.NewMinPercent - previousMinTarget;
								fullCorrectionDelta[firstWriteRow.Slot] = firstWriteRow.NewMaxPercent - previousMaxTarget;
								await WriteAdjustedCoefficientsAsync(firstWriteRow, $"第{attempt}轮-两点联合首写", ct);
							}
							catch (Exception ex2) when (!(ex2 is OperationCanceledException))
							{
								MarkFailed(firstWriteRow, ex2.Message);
								pending.Remove(firstWriteRow);
							}
						}
						pending = ActiveRows(pending);
						await MeasureBatchAsync(pending, (double)_p0.Value, $"第{attempt}轮-首写零点复测批量", $"第{attempt}轮-首写零点复测", delegate(F40SlotRow row, double v)
						{
							row.ZeroV = v;
						});
						for (int step = 1; step <= OriginalRecoveryMaxSteps; step++)
						{
							List<F40SlotRow> zeroPending = (from f40SlotRow in ActiveRows(pending)
								where !IsVoltageInRange(f40SlotRow.ZeroV, CalibrationTargetMinV, tol)
								select f40SlotRow).ToList();
							if (zeroPending.Count == 0)
							{
								break;
							}
							foreach (F40SlotRow r4 in zeroPending.ToList())
							{
								ct.ThrowIfCancellationRequested();
								try
								{
									r4.Status = $"第{attempt}轮-零点修正{step}";
									_grid.Refresh();
									r4.ApplyZeroRecoveryStep(zeroCorrectionDelta[r4.Slot], fullCorrectionDelta[r4.Slot]);
									Log($"Slot{r4.Slot} 原版零点重复修正：测量={r4.ZeroV:0.######}V，重复首轮低点增量{zeroCorrectionDelta[r4.Slot]:+0.##;-0.##}%p、满点增量{fullCorrectionDelta[r4.Slot]:+0.##;-0.##}%p");
									await WriteAdjustedCoefficientsAsync(r4, $"第{attempt}轮-写零点系数{step}", ct);
								}
								catch (Exception ex3) when (!(ex3 is OperationCanceledException))
								{
									MarkFailed(r4, ex3.Message);
									pending.Remove(r4);
								}
								_grid.Refresh();
							}
							zeroPending = ActiveRows(zeroPending);
							await MeasureBatchAsync(zeroPending, (double)_p0.Value, $"第{attempt}轮-零点复测批量{step}", $"第{attempt}轮-零点复测{step}", delegate(F40SlotRow f40SlotRow, double v)
							{
								f40SlotRow.ZeroV = v;
							});
							pending = ActiveRows(pending);
						}
					}
					foreach (F40SlotRow r5 in (from f40SlotRow in ActiveRows(pending)
						where !IsVoltageInRange(f40SlotRow.ZeroV, CalibrationTargetMinV, tol)
						select f40SlotRow).ToList())
					{
						failCount++;
						r5.Status = $"完成/不合格：零点{OriginalRecoveryMaxSteps}次未达标";
						Log($"Slot{r5.Slot} 零点步进修正{OriginalRecoveryMaxSteps}次仍未达到{FormatVoltageTarget(CalibrationTargetMinV)}，跳过当前工位：{BuildCalibrationFailReason(r5.ZeroV, r5.PreFullV, tol)}", important: true);
						pending.Remove(r5);
					}
					pending = ActiveRows(pending);
					await MeasureBatchAsync(pending, (double)_pfull.Value, $"第{attempt}轮-满点起始批量", $"第{attempt}轮-满点起始", delegate(F40SlotRow f40SlotRow, double v)
					{
						f40SlotRow.FullV = v;
					});
					pending = ActiveRows(pending);
					if (_writeBoard.Checked)
					{
						for (int step2 = 1; step2 <= OriginalRecoveryMaxSteps; step2++)
						{
							List<F40SlotRow> fullPending = (from f40SlotRow in ActiveRows(pending)
								where !IsVoltageInRange(f40SlotRow.FullV, CalibrationTargetMaxV, tol)
								select f40SlotRow).ToList();
							if (fullPending.Count == 0)
							{
								break;
							}
							foreach (F40SlotRow r6 in fullPending.ToList())
							{
								ct.ThrowIfCancellationRequested();
								try
								{
									r6.Status = $"第{attempt}轮-满点修正{step2}";
									_grid.Refresh();
									r6.ApplyFullRecoveryStep(fullCorrectionDelta[r6.Slot]);
									Log($"Slot{r6.Slot} 原版满点重复修正：测量={r6.FullV:0.######}V，重复首轮满点增量{fullCorrectionDelta[r6.Slot]:+0.##;-0.##}%p");
									await WriteAdjustedCoefficientsAsync(r6, $"第{attempt}轮-写满点系数{step2}", ct);
								}
								catch (Exception ex4) when (!(ex4 is OperationCanceledException))
								{
									MarkFailed(r6, ex4.Message);
									pending.Remove(r6);
								}
								_grid.Refresh();
							}
							fullPending = ActiveRows(fullPending);
							await MeasureBatchAsync(fullPending, (double)_pfull.Value, $"第{attempt}轮-满点复测批量{step2}", $"第{attempt}轮-满点复测{step2}", delegate(F40SlotRow f40SlotRow, double v)
							{
								f40SlotRow.FullV = v;
							});
							pending = ActiveRows(pending);
						}
					}
					foreach (F40SlotRow r7 in (from f40SlotRow in ActiveRows(pending)
						where !IsVoltageInRange(f40SlotRow.FullV, CalibrationTargetMaxV, tol)
						select f40SlotRow).ToList())
					{
						failCount++;
						r7.Status = $"完成/不合格：满点{OriginalRecoveryMaxSteps}次未达标";
						Log($"Slot{r7.Slot} 满点步进修正{OriginalRecoveryMaxSteps}次仍未达到{FormatVoltageTarget(CalibrationTargetMaxV)}，跳过当前工位：{BuildCalibrationFailReason(r7.ZeroV, r7.FullV, tol)}", important: true);
						pending.Remove(r7);
					}
					pending = ActiveRows(pending);
					if (_verifyAfterWrite.Checked && CalibrationLinearityEnabled)
					{
						List<F40SlotRow> midVerifyRows = pending.Where((F40SlotRow f40SlotRow) => IsVoltageInRange(f40SlotRow.ZeroV, CalibrationTargetMinV, tol) && IsVoltageInRange(f40SlotRow.FullV, CalibrationTargetMaxV, tol)).ToList();
						await MeasureBatchAsync(midVerifyRows, (double)_pmid.Value, $"第{attempt}轮-复测中点批量", $"第{attempt}轮-复测中点", delegate(F40SlotRow f40SlotRow, double v)
						{
							f40SlotRow.PostMidV = v;
							f40SlotRow.LinearityPercent = F40SlotRow.Linearity(v, f40SlotRow.ZeroV, f40SlotRow.FullV);
						});
						pending = ActiveRows(pending);
					}
					foreach (F40SlotRow r8 in pending.ToList())
					{
						double verifyLow = r8.ZeroV;
						double verifyHigh = r8.FullV;
						WarnIfWriteDidNotMoveOutput(r8, verifyLow, verifyHigh);
						bool passed = IsVoltageInRange(verifyLow, CalibrationTargetMinV, tol) && IsVoltageInRange(verifyHigh, CalibrationTargetMaxV, tol);
						string finalReason = BuildCalibrationFailReason(verifyLow, verifyHigh, tol);
						if (passed)
						{
							if (!double.IsNaN(r8.PostMidV))
							{
								Log($"Slot{r8.Slot} 复测：低={verifyLow:0.######}V 满={verifyHigh:0.######}V 中={r8.PostMidV:0.######}V 线性={r8.LinearityPercent:0.###}%", important: true);
							}
							else
							{
								Log($"Slot{r8.Slot} 复测：低={verifyLow:0.######}V 满={verifyHigh:0.######}V", important: true);
							}
							okCount++;
							passedSlots.Add(r8.Slot);
							r8.Status = "完成/合格";
							Log($"Slot{r8.Slot} 批量标定合格：写前低={r8.PreZeroV:0.######}V 写前满={r8.PreFullV:0.######}V 零点={verifyLow:0.######}V 满点={verifyHigh:0.######}V", important: true);
							pending.Remove(r8);
						}
						else if (!_writeBoard.Checked)
						{
							failCount++;
							r8.Status = "完成/不合格：未写入板卡";
							Log($"Slot{r8.Slot} 未勾选写入板卡0x11，无法按原 F40 流程完成零点/满点修正：{finalReason}", important: true);
							pending.Remove(r8);
						}
						else if (!unlimitedRetries && TryMarkWriteIneffectiveFailure(r8, verifyLow, verifyHigh, tol))
						{
							failCount++;
							pending.Remove(r8);
						}
						else if (!unlimitedRetries && attempt >= maxRetries)
						{
							failCount++;
							r8.Status = "完成/不合格：" + finalReason;
							Log($"Slot{r8.Slot} 达到最大复标次数仍不合格：{finalReason}", important: true);
							pending.Remove(r8);
						}
						else
						{
							r8.Status = $"第{attempt + 1}轮-待继续";
							Log($"Slot{r8.Slot} 第{attempt}轮后仍未达标，保留到下一轮：{finalReason}", important: true);
						}
					}
					Log($"第{attempt}轮批量稳压完成：本轮后剩余待标定 {pending.Count} 个，累计合格 {okCount} 个，不合格/跳过 {failCount} 个。", important: true);
					_grid.Refresh();
					attempt++;
				}
				Log($"批量稳压标定流程完成：合格/完成 {okCount} 个，不合格/跳过 {failCount} 个。", important: true);
			}
			finally
			{
				foreach (VisaInstrument d in dmms.Values)
				{
					d.Dispose();
				}
			}
		}
		finally
		{
			if (pressure != null)
			{
				((IDisposable)pressure).Dispose();
			}
		}
		List<F40SlotRow> ActiveRows(IEnumerable<F40SlotRow> rows)
		{
			return rows.Where((F40SlotRow f40SlotRow) => !passedSlots.Contains(f40SlotRow.Slot) && !IsSkipped(f40SlotRow)).ToList();
		}
		VisaInstrument? GetDmm(F40SlotRow row)
		{
			if (!_useGpib.Checked)
			{
				return null;
			}
			string text = (_useDaqChannel.Checked ? row.DmmAddress : _dmmAddr.Text.Trim());
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new InvalidOperationException($"Slot{row.Slot} 未配置DAQ/DMM GPIB地址，请检查多DAQ配置。");
			}
			if (!dmms.TryGetValue(text, out VisaInstrument value))
			{
				value = new VisaInstrument("DMM/DAQ " + text, text, Log);
				value.Open();
				value.Query(CommandFor(_dmmModel.Text, "MachineType", "*IDN?"));
				dmms[text] = value;
			}
			return value;
		}
		static bool IsSkipped(F40SlotRow row)
		{
			return row.Status.StartsWith("不合格/跳过", StringComparison.OrdinalIgnoreCase);
		}
		void MarkFailed(F40SlotRow row, string message)
		{
			failCount++;
			row.Status = "不合格/跳过：" + ShortError(message);
			Log($"Slot{row.Slot} 批量标定失败，已跳过：{message}", important: true);
		}
		async Task MeasureBatchAsync(IReadOnlyList<F40SlotRow> rows, double pressureUserUnit, string pressureTag, string measureTag, Action<F40SlotRow, double> assign)
		{
			if (rows.Count != 0)
			{
				await SetPressureAndWaitAsync(pressure, pressureUserUnit, pressureTag, ct);
				foreach (F40SlotRow row in rows.ToList())
				{
					ct.ThrowIfCancellationRequested();
					try
					{
						row.Status = measureTag;
						_grid.Refresh();
						assign(row, await MeasureOutputAsync(GetDmm(row), row, measureTag, ct));
					}
					catch (Exception ex4) when (!(ex4 is OperationCanceledException))
					{
						MarkFailed(row, ex4.Message);
					}
					_grid.Refresh();
				}
			}
		}
	}

	private async Task SetPressureAndWaitAsync(VisaInstrument? pressure, double pressureUserUnit, string tag, CancellationToken ct)
	{
		if (pressure != null)
		{
			double kpa = ToKpa(pressureUserUnit);
			pressure.Write(CommandFor(_pressureModel.Text, "SetPressure", "*CLS;UNIT KPa;:Sour:PRES 9999;:OUTPUT ON", kpa.ToString("0.######", CultureInfo.InvariantCulture)));
			Log(tag + "：设置压力 " + FormatPressureUser(pressureUserUnit));
			await WaitPressureStableAsync(pressure, kpa, ct);
		}
		else
		{
			Log($"未启用GPIB：请人工设置{tag}压力 {FormatPressureUser(pressureUserUnit)}，{(double)_settleSec.Value:0.0}s后读数/或手填");
			await Task.Delay(TimeSpan.FromSeconds((double)_settleSec.Value), ct);
		}
		await Task.Delay(TimeSpan.FromSeconds((double)_settleSec.Value), ct);
	}

	private Task<double> MeasureOutputAsync(VisaInstrument? dmm, F40SlotRow row, string tag, CancellationToken ct)
	{
		if (dmm == null)
		{
			using (InputBox inputBox = new InputBox($"输入 Slot{row.Slot} {tag}输出电压(V)", "0"))
			{
				if (inputBox.ShowDialog(this) != DialogResult.OK)
				{
					throw new OperationCanceledException();
				}
				return Task.FromResult(double.Parse(inputBox.Value, CultureInfo.InvariantCulture));
			}
		}
		string channel = row.Channel;
		double num;
		if (_useDaqChannel.Checked)
		{
			if (string.IsNullOrWhiteSpace(channel))
			{
				throw new InvalidOperationException($"Slot{row.Slot} 没有DAQ通道映射。DAQ973A60模式只支持Slot1..60。当前Slot={row.Slot}");
			}
			dmm.Write(CommandFor(_dmmModel.Text, "Close", "ROUT:CLOS (@9999)", channel));
			dmm.Write(CommandFor(_dmmModel.Text, "SetVol", "CONF:VOLT (@9999)", channel));
			num = dmm.QueryNumber(CommandFor(_dmmModel.Text, "ReadValue", "READ?"));
			try
			{
				dmm.Write(CommandFor(_dmmModel.Text, "Open", "ROUT:OPEN (@9999)", channel));
			}
			catch
			{
			}
		}
		else
		{
			dmm.Write(CommandFor(_dmmModel.Text, "SetVol", "CONF:VOLT"));
			num = dmm.QueryNumber(CommandFor(_dmmModel.Text, "ReadValue", "READ?"));
		}
		Log($"Slot{row.Slot} {tag}输出值：{num:0.######} V", important: true);
		return Task.FromResult(num);
	}

	private async Task<double> MeasureAtPressureAsync(VisaInstrument? pressure, VisaInstrument? dmm, F40SlotRow row, double pressureUserUnit, string tag, CancellationToken ct)
	{
		await SetPressureAndWaitAsync(pressure, pressureUserUnit, $"Slot{row.Slot} {tag}", ct);
		return await MeasureOutputAsync(dmm, row, tag, ct);
	}

	private async Task WaitPressureStableAsync(VisaInstrument pressure, double targetKpa, CancellationToken ct)
	{
		double configuredTol = (double)_stableTolKpa.Value;
		double tol = ((Math.Abs(targetKpa) < 1E-09) ? Math.Max(configuredTol, 0.5) : configuredTol);
		TimeSpan need = TimeSpan.FromSeconds((double)_stableSec.Value);
		TimeSpan progressInterval = TimeSpan.FromSeconds(10.0);
		DateTime? since = null;
		Stopwatch sw = Stopwatch.StartNew();
		TimeSpan lastWaitingLog = TimeSpan.MinValue;
		TimeSpan lastHoldingLog = TimeSpan.MinValue;
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			double p = pressure.QueryNumberSilent(CommandFor(_pressureModel.Text, "ReadPressure", "*CLS;SENS?"));
			double delta = Math.Abs(p - targetKpa);
			bool ok = delta <= tol;
			string currentText = FormatPressureFromKpa(p);
			string targetText = FormatPressureFromKpa(targetKpa);
			string deltaText = FormatPressureFromKpa(delta);
			string tolText = FormatPressureFromKpa(tol);
			if (ok)
			{
				since.GetValueOrDefault();
				if (!since.HasValue)
				{
					DateTime now = DateTime.Now;
					since = now;
				}
				if (lastHoldingLog == TimeSpan.MinValue || sw.Elapsed - lastHoldingLog >= progressInterval)
				{
					double held = Math.Min(need.TotalSeconds, (!since.HasValue) ? 0.0 : (DateTime.Now - since.Value).TotalSeconds);
					Log($"压力读数 {currentText}，目标 {targetText}，偏差 {deltaText} ≤ 容差 {tolText}，稳定保持{held:0}/{need.TotalSeconds:0}s");
					lastHoldingLog = sw.Elapsed;
				}
			}
			else
			{
				since = null;
				lastHoldingLog = TimeSpan.MinValue;
				if (lastWaitingLog == TimeSpan.MinValue || sw.Elapsed - lastWaitingLog >= progressInterval)
				{
					Log($"压力读数 {currentText}，目标 {targetText}，偏差 {deltaText} > 容差 {tolText}，稳定中");
					lastWaitingLog = sw.Elapsed;
				}
			}
			if (need.TotalSeconds <= 0.0 || (since.HasValue && DateTime.Now - since.Value >= need))
			{
				Log($"压力稳定完成：当前 {currentText}，目标 {targetText}，偏差 {deltaText}，容差 {tolText}");
				return;
			}
			if (sw.Elapsed > TimeSpan.FromMinutes(5.0))
			{
				break;
			}
			await Task.Delay(1000, ct);
		}
		throw new TimeoutException("压力稳定超时5分钟");
	}

	private static bool IsPsiUnit(string? unit)
	{
		return string.Equals(unit?.Trim(), "psi", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizePressureUnit(string? unit)
	{
		return IsPsiUnit(unit) ? "psi" : "kPa";
	}

	private static double ConvertPressureToKpa(double value, string? unit)
	{
		return IsPsiUnit(unit) ? (value * 6.894757293168361) : value;
	}

	private static double ConvertPressureFromKpa(double value, string? unit)
	{
		return IsPsiUnit(unit) ? (value / 6.894757293168361) : value;
	}

	private static string FormatPressureValue(double value, string? unit, int decimals = 3)
	{
		return value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture) + NormalizePressureUnit(unit);
	}

	private double ToKpa(double value)
	{
		return ConvertPressureToKpa(value, _pressureUnit.Text);
	}

	private double FromKpa(double value)
	{
		return ConvertPressureFromKpa(value, _pressureUnit.Text);
	}

	private string FormatPressureUser(double value)
	{
		return FormatPressureValue(value, _pressureUnit.Text);
	}

	private string FormatPressureFromKpa(double kpa)
	{
		return FormatPressureValue(FromKpa(kpa), _pressureUnit.Text);
	}

	private List<BoardSlotRoute> GetBoardSlotRoutes()
	{
		string text = _boardSlotMap.Text.Trim();
		List<BoardSlotRoute> list = new List<BoardSlotRoute>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return list;
		}
		string[] array = text.Split(new char[4] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string text2 in array)
		{
			string text3 = text2.Trim();
			Match match = Regex.Match(text3, "^(?:板卡)?(?<addr>\\d+)\\s*[:=]\\s*(?<from>\\d+)\\s*-\\s*(?<to>\\d+)$", RegexOptions.IgnoreCase);
			if (!match.Success)
			{
				throw new FormatException("板卡范围格式错误：" + text3 + "，示例：1=1-80;2=81-160");
			}
			int num = int.Parse(match.Groups["addr"].Value, CultureInfo.InvariantCulture);
			int num2 = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
			int num3 = int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture);
			if (num2 > num3)
			{
				int num4 = num3;
				num3 = num2;
				num2 = num4;
			}
			if ((num < 1 || num > 247) ? true : false)
			{
				throw new FormatException($"板卡地址超范围：{num}");
			}
			if (num2 < 1 || num3 > 255)
			{
				throw new FormatException($"工位范围超范围：{num2}-{num3}");
			}
			list.Add(new BoardSlotRoute((byte)num, num2, num3));
		}
		return list.OrderBy((BoardSlotRoute x) => x.FromSlot).ToList();
	}

	private int[] GetBoardPhysicalSlots()
	{
		if (_useBoardChannel47.Checked)
		{
			return Enumerable.Range(1, 80).ToArray();
		}
		return GetSkip47PhysicalSlots();
	}

	private static int[] GetSkip47PhysicalSlots()
	{
		return Enumerable.Range(1, 24).Concat(Enumerable.Range(33, 16)).Concat(Enumerable.Range(57, 24))
			.ToArray();
	}

	private BoardSlotTarget ResolveBoardSlotFromStart(int startBoard, int startLocalLogicalSlot, int offset)
	{
		int[] boardPhysicalSlots = GetBoardPhysicalSlots();
		int num = startLocalLogicalSlot - 1 + offset;
		if (num < 0)
		{
			throw new ArgumentOutOfRangeException("startLocalLogicalSlot");
		}
		byte boardAddr;
		byte localSlot;
		checked
		{
			boardAddr = (byte)(startBoard + unchecked(num / boardPhysicalSlots.Length));
			localSlot = (byte)boardPhysicalSlots[unchecked(num % boardPhysicalSlots.Length)];
		}
		return new BoardSlotTarget(boardAddr, localSlot, offset + 1);
	}

	private BoardSlotTarget ResolveBoardSlot(int globalSlot)
	{
		int[] boardPhysicalSlots = GetBoardPhysicalSlots();
		List<BoardSlotRoute> boardSlotRoutes = GetBoardSlotRoutes();
		if (boardSlotRoutes.Count > 0)
		{
			int fromSlot = boardSlotRoutes[0].FromSlot;
			if (globalSlot >= fromSlot)
			{
				int num = globalSlot - fromSlot;
				int num2 = num / boardPhysicalSlots.Length;
				checked
				{
					int num3;
					if (num2 >= boardSlotRoutes.Count)
					{
						num3 = (byte)(boardSlotRoutes[unchecked(boardSlotRoutes.Count - 1)].BoardAddr + num2 - boardSlotRoutes.Count + 1);
					}
					else
					{
						num3 = boardSlotRoutes[num2].BoardAddr;
					}
					byte boardAddr = unchecked((byte)num3);
					byte localSlot = (byte)boardPhysicalSlots[unchecked(num % boardPhysicalSlots.Length)];
					return new BoardSlotTarget(boardAddr, localSlot, globalSlot);
				}
			}
		}
		int num4 = globalSlot - 1;
		checked
		{
			byte boardAddr2 = (byte)((int)_addr.Value + unchecked(num4 / boardPhysicalSlots.Length));
			byte localSlot2 = (byte)boardPhysicalSlots[unchecked(num4 % boardPhysicalSlots.Length)];
			return new BoardSlotTarget(boardAddr2, localSlot2, globalSlot);
		}
	}

	private List<DaqProfile> GetDaqProfiles()
	{
		try
		{
			_daqProfileGrid.EndEdit();
			SyncDaqProfilesTextFromGrid();
		}
		catch
		{
		}
		List<DaqProfile> list = new List<DaqProfile>();
		if (!_multiDaq.Checked)
		{
			list.Add(new DaqProfile(1, 255, _dmmAddr.Text.Trim(), _channelExpr.Text.Trim()));
			return list;
		}
		string[] array = _daqProfiles.Text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			string text2 = text.Trim();
			if (text2.Length != 0 && !text2.StartsWith("#"))
			{
				string[] array2 = text2.Split('=', 2);
				if (array2.Length != 2)
				{
					throw new FormatException("DAQ配置格式错误：" + text2);
				}
				string text3 = array2[0].Trim();
				string[] array3 = array2[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (array3.Length < 1)
				{
					throw new FormatException("DAQ配置缺少GPIB地址：" + text2);
				}
				Match match = Regex.Match(text3, "^(\\d+)\\s*-\\s*(\\d+)$");
				if (!match.Success)
				{
					throw new FormatException("DAQ范围格式错误：" + text3 + "，示例 1-60");
				}
				int num = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
				int num2 = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
				if (num > num2)
				{
					int num3 = num2;
					num2 = num;
					num = num3;
				}
				list.Add(new DaqProfile(num, num2, array3[0], (array3.Length >= 2) ? array3[1] : _channelExpr.Text.Trim()));
			}
		}
		return list;
	}

	private DaqProfile? FindDaqProfile(int slot)
	{
		return GetDaqProfiles().FirstOrDefault((DaqProfile p) => slot >= p.FromSlot && slot <= p.ToSlot);
	}

	private bool IsSlotCoveredByDaqConfig(int slot)
	{
		return !_useDaqChannel.Checked || !string.IsNullOrWhiteSpace(EvalChannel(slot));
	}

	private string EvalDmmAddress(int slot)
	{
		if (!_useDaqChannel.Checked)
		{
			return _dmmAddr.Text.Trim();
		}
		return FindDaqProfile(slot)?.Address ?? "";
	}

	private string EvalChannel(int slot)
	{
		string? manualChannel = TryEvalManualChannelOverride(slot);
		if (!string.IsNullOrWhiteSpace(manualChannel))
		{
			return manualChannel;
		}
		string text = _channelExpr.Text.Trim();
		int num = slot;
		if (_useDaqChannel.Checked)
		{
			DaqProfile daqProfile = FindDaqProfile(slot);
			if ((object)daqProfile == null)
			{
				return "";
			}
			text = (string.IsNullOrWhiteSpace(daqProfile.Map) ? text : daqProfile.Map.Trim());
			num = GetDaqLocalSlot(slot, daqProfile.FromSlot);
			if (num <= 0)
			{
				return "";
			}
		}
		if (text.Length == 0)
		{
			return "";
		}
		if (text.Equals("DAQ973A60", StringComparison.OrdinalIgnoreCase) || text.Equals("DAQ973A-60", StringComparison.OrdinalIgnoreCase) || text.Contains("101-120", StringComparison.OrdinalIgnoreCase))
		{
			if (num < 1 || num > 60)
			{
				return "";
			}
			int num2 = (num - 1) / 20 + 1;
			int num3 = (num - 1) % 20 + 1;
			return (num2 * 100 + num3).ToString(CultureInfo.InvariantCulture);
		}
		string text2 = text.Replace("LocalSlot", num.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).Replace("Slot", slot.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
		try
		{
			if (Regex.IsMatch(text2, "^\\s*\\d+\\s*([+\\-]\\s*\\d+\\s*)?$"))
			{
				return (from m in Regex.Matches(text2, "[+\\-]?\\s*\\d+")
					select int.Parse(m.Value.Replace(" ", ""), CultureInfo.InvariantCulture)).Sum().ToString(CultureInfo.InvariantCulture);
			}
		}
		catch
		{
		}
		return text2;
	}

	private string? TryEvalManualChannelOverride(int slot)
	{
		string text = _daqChannelOverrideMap.Text.Trim();
		if (text.Length == 0)
		{
			return null;
		}
		string[] entries = text.Split(new char[4] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string entry in entries)
		{
			string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
			if (parts.Length != 2)
			{
				throw new FormatException("手动通道覆盖格式错误：" + entry + "，示例 1=101;9=102 或 1-20=101-120");
			}
			if (!TryParseIntRange(parts[0], out int fromSlot, out int toSlot) || !TryParseIntRange(parts[1], out int fromChannel, out int toChannel))
			{
				throw new FormatException("手动通道覆盖范围错误：" + entry + "，示例 1=101;9=102 或 1-20=101-120");
			}
			if (fromSlot > toSlot)
			{
				(fromSlot, toSlot) = (toSlot, fromSlot);
			}
			if (fromChannel > toChannel)
			{
				(fromChannel, toChannel) = (toChannel, fromChannel);
			}
			if (slot < fromSlot || slot > toSlot)
			{
				continue;
			}
			int slotCount = toSlot - fromSlot;
			int channelCount = toChannel - fromChannel;
			if (slotCount != channelCount)
			{
				throw new FormatException($"手动通道覆盖数量不一致：{entry}");
			}
			return (fromChannel + slot - fromSlot).ToString(CultureInfo.InvariantCulture);
		}
		return null;
	}

	private static bool TryParseIntRange(string text, out int from, out int to)
	{
		Match match = Regex.Match(text.Trim(), "^(?<from>\\d+)(?:\\s*-\\s*(?<to>\\d+))?$");
		if (!match.Success)
		{
			from = 0;
			to = 0;
			return false;
		}
		from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
		to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture) : from;
		return true;
	}

	private int GetDaqLocalSlot(int globalSlot, int profileFromSlot)
	{
		if (!_daqSkipChannel47.Checked)
		{
			return globalSlot - profileFromSlot + 1;
		}
		int num = globalSlot - profileFromSlot;
		if (num < 0)
		{
			return -1;
		}
		int num2 = num / 80;
		int value = num % 80 + 1;
		int[] skip47PhysicalSlots = GetSkip47PhysicalSlots();
		int num3 = Array.IndexOf(skip47PhysicalSlots, value);
		return (num3 < 0) ? (-1) : (num2 * skip47PhysicalSlots.Length + num3 + 1);
	}

	private void Log(string text, bool important = false)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				Log(text, important);
			});
			return;
		}
		DateTime now = DateTime.Now;
		string text2 = $"[{now:HH:mm:ss}] {(important ? "* " : "")}{text}";
		string text3 = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {(important ? "* " : "")}{text}";
		_log.AppendText(text2 + Environment.NewLine);
		_logFull.AppendText(text3 + Environment.NewLine);
		_logManual.AppendText(text2 + Environment.NewLine);
		if (important || _cts != null)
		{
			UpdateCalibrationOverview(text);
		}
		try
		{
			File.AppendAllText(_logFile, text3 + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}
}
