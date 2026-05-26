using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Devices;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// Manual debug page (recreated to match the original ASLab layout):
///  - 设备控制 + 通道 + 电源切换 + 串口设置 cards
///  - 6 路读取数据
///  - 阀门 1..8 + 排气阀
///  - 两个大底栏: 数据I/O (TX/RX 原始报文)  +  历史记录 (操作日志)
/// </summary>
public sealed class ManualViewModel : ViewModelBase
{
    private readonly TestSession _session;
    private readonly DeviceStatusVm? _ovenStatus;
    private readonly DeviceStatusVm? _dacStatus;
    private SerialPort? _ovenSerial;

    // ─── COM / serial profile (display-only for SIM) ────────────────────────
    public ObservableCollection<string> ComPorts { get; } = new();

    public RelayCommand RefreshComPortsCommand { get; }

    private void RefreshComPorts()
    {
        var current = System.IO.Ports.SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ComPorts.Clear();
        foreach (var p in current) ComPorts.Add(p);
        if (current.Length == 0) ComPorts.Add("(无可用串口)");
        if (!ComPorts.Contains(CardCom)) CardCom = ComPorts[0];
        if (!ComPorts.Contains(OvenCom)) OvenCom = ComPorts.Count > 1 ? ComPorts[1] : ComPorts[0];
        OnPropertyChanged(nameof(CardCom));
        OnPropertyChanged(nameof(OvenCom));
    }
    public ObservableCollection<string> BaudRates { get; } =
        new(new[] { "9600", "19200", "38400", "57600", "115200" });
    public ObservableCollection<string> ParityList { get; } =
        new(new[] { "None", "Odd", "Even" });
    public ObservableCollection<string> StopBits { get; } =
        new(new[] { "1", "1.5", "2" });
    public ObservableCollection<string> DataBits { get; } =
        new(new[] { "7", "8" });

    private string _cardCom = "COM3";
    public string CardCom { get => _cardCom; set => SetField(ref _cardCom, value); }
    public string CardBaud { get; set; } = "9600";
    public string CardParity { get; set; } = "None";
    public string CardStop { get; set; } = "1";
    public string CardData { get; set; } = "8";
    private bool _isCardSerialOpen;
    public bool IsCardSerialOpen
    {
        get => _isCardSerialOpen;
        set
        {
            if (!SetField(ref _isCardSerialOpen, value)) return;
            OnPropertyChanged(nameof(CardSerialButtonText));
            OnPropertyChanged(nameof(CardSerialStatusText));
        }
    }
    public string CardSerialButtonText => IsCardSerialOpen ? "关闭串口" : "打开串口";
    public string CardSerialStatusText => IsCardSerialOpen ? $"已连接 {CardCom}" : "未连接";

    private string _ovenCom = "COM5";
    public string OvenCom { get => _ovenCom; set => SetField(ref _ovenCom, value); }
    public string OvenBaud { get; set; } = "19200";
    public string OvenParity { get; set; } = "None";
    public string OvenStop { get; set; } = "1";
    public string OvenData { get; set; } = "8";
    private bool _isOvenSerialOpen;
    public bool IsOvenSerialOpen
    {
        get => _isOvenSerialOpen;
        set
        {
            if (!SetField(ref _isOvenSerialOpen, value)) return;
            OnPropertyChanged(nameof(OvenSerialButtonText));
            OnPropertyChanged(nameof(OvenSerialStatusText));
        }
    }
    public string OvenSerialButtonText => IsOvenSerialOpen ? "关闭串口" : "打开串口";
    public string OvenSerialStatusText => IsOvenSerialOpen ? $"已连接 {OvenCom}" : "未连接";

    // ─── 通道选择 ───────────────────────────────────────────────────────────
    public ObservableCollection<string> CardAddrs { get; } =
        new(Enumerable.Range(1, 16).Select(i => i.ToString()));
    public ObservableCollection<string> ChannelAddrs { get; } =
        new(Enumerable.Range(1, 16).Select(i => i.ToString()));

    private string _cardAddr = "1";
    public string CardAddr
    {
        get => _cardAddr;
        set
        {
            if (SetField(ref _cardAddr, value))
                OnPropertyChanged(nameof(UtPowerText));
        }
    }
    private string _channelAddr = "1";
    public string ChannelAddr { get => _channelAddr; set => SetField(ref _channelAddr, value); }

    // ─── 压控 ──────────────────────────────────────────────────────────────
    public ObservableCollection<string> PressurePorts { get; } =
        new(new[] { "0", "1", "2", "3" });
    public ObservableCollection<string> PressureAddrs { get; } =
        new(new[] { "1", "5", "10", "11" });
    public ObservableCollection<string> PressureModels { get; } =
        new(new[] { "FLUKE-7250", "FLUKE-6270", "WIKA-CPC8000" });
    public ObservableCollection<string> PressureUnits { get; } =
        new(new[] { "kPa", "MPa", "bar", "psi" });
    public ObservableCollection<string> MeasureModes { get; } =
        new(new[] { "Absolute", "Gauge" });

    private string _pressurePort = "1";
    public string PressurePort { get => _pressurePort; set => SetField(ref _pressurePort, value); }
    private string _pressureAddr = "10";
    public string PressureAddr { get => _pressureAddr; set => SetField(ref _pressureAddr, value); }
    private string _pressureModel = "FLUKE-7250";
    public string PressureModel { get => _pressureModel; set => SetField(ref _pressureModel, value); }
    private string _pressureUnit = "kPa";
    public string PressureUnit { get => _pressureUnit; set => SetField(ref _pressureUnit, value); }
    private string _measureMode = "Gauge";
    public string MeasureMode { get => _measureMode; set => SetField(ref _measureMode, value); }

    private float _targetPressure = 100f;
    public float TargetPressure { get => _targetPressure; set => SetField(ref _targetPressure, value); }

    private float _precision = 0.05f;
    public float Precision { get => _precision; set => SetField(ref _precision, value); }

    private string _readPressureText = "";
    public string ReadPressureText { get => _readPressureText; set => SetField(ref _readPressureText, value); }
    private string _readRangeText = "";
    public string ReadRangeText { get => _readRangeText; set => SetField(ref _readRangeText, value); }
    private string _readModelText = "";
    public string ReadModelText { get => _readModelText; set => SetField(ref _readModelText, value); }
    private string _readStatusText = "";
    public string ReadStatusText { get => _readStatusText; set => SetField(ref _readStatusText, value); }
    private string _manualScpi = "";
    public string ManualScpi { get => _manualScpi; set => SetField(ref _manualScpi, value); }
    private string _manualScpiReply = "";
    public string ManualScpiReply { get => _manualScpiReply; set => SetField(ref _manualScpiReply, value); }

    // ─── 烘箱 ──────────────────────────────────────────────────────────────
    private float _targetTemp = 25f;
    public float TargetTemp { get => _targetTemp; set => SetField(ref _targetTemp, value); }

    private string _readOvenText = "";
    public string ReadOvenText { get => _readOvenText; set => SetField(ref _readOvenText, value); }

    // ─── 电源 ──────────────────────────────────────────────────────────────
    private string _currentVoltage = "—";
    public string CurrentVoltage { get => _currentVoltage; set => SetField(ref _currentVoltage, value); }

    private string _closedUtPowerCard = "";
    public string UtPowerText => string.Equals(_closedUtPowerCard, CardAddr, StringComparison.OrdinalIgnoreCase)
        ? $"UT：板卡{CardAddr}关闭 ({GetSelectedUtPowerChannel()})"
        : $"UT：默认开启 ({GetSelectedUtPowerChannel()})";

    // ─── 读取数据 ───────────────────────────────────────────────────────────
    private string _readDriveV = ""; public string ReadDriveV { get => _readDriveV; set => SetField(ref _readDriveV, value); }
    private string _readDriveI = ""; public string ReadDriveI { get => _readDriveI; set => SetField(ref _readDriveI, value); }
    private string _readUsig   = ""; public string ReadUsig   { get => _readUsig;   set => SetField(ref _readUsig,   value); }
    private string _readUT     = ""; public string ReadUT     { get => _readUT;     set => SetField(ref _readUT,     value); }
    private string _readT      = ""; public string ReadT      { get => _readT;      set => SetField(ref _readT,      value); }
    private string _read40uA   = ""; public string Read40uA   { get => _read40uA;   set => SetField(ref _read40uA,   value); }

    // ─── 阀门 ──────────────────────────────────────────────────────────────
    public ObservableCollection<ValveVm> Valves { get; } = new();

    // ─── 多点采集 ───────────────────────────────────────────────────────────
    public ObservableCollection<string> SlotNames { get; } = new();
    public ObservableCollection<string> MeasureKinds { get; } = new()
    {
        "Usig", "UT", "Usource", "Isource", "DMM_V", "DMM_R"
    };

    private string _selectedSlot = "Slot1";
    public string SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (SetField(ref _selectedSlot, value))
                ApplySelectedSlotMapping();
        }
    }
    private string _selectedMeasure = "Usig";
    public string SelectedMeasure { get => _selectedMeasure; set => SetField(ref _selectedMeasure, value); }
    private string _manualLabel = "MANUAL";
    public string ManualLabel { get => _manualLabel; set => SetField(ref _manualLabel, value); }

    // ─── 两个底栏数据 ───────────────────────────────────────────────────────
    /// <summary>"数据I/O" 面板：原始 TX/RX 报文。</summary>
    public ObservableCollection<string> DataIo { get; } = new();
    /// <summary>"历史记录" 面板：操作动作日志。</summary>
    public ObservableCollection<string> History { get; } = new();

    private string _dataIoText = "";
    public string DataIoText { get => _dataIoText; private set => SetField(ref _dataIoText, value); }

    private string _historyText = "";
    public string HistoryText { get => _historyText; private set => SetField(ref _historyText, value); }

    private bool _ioShowTx = true, _ioShowRx = true, _ioShowInfo = false, _ioAutoScroll = true;
    public bool IoShowTx     { get => _ioShowTx;     set => SetField(ref _ioShowTx,     value); }
    public bool IoShowRx     { get => _ioShowRx;     set => SetField(ref _ioShowRx,     value); }
    public bool IoShowInfo   { get => _ioShowInfo;   set => SetField(ref _ioShowInfo,   value); }
    public bool IoAutoScroll { get => _ioAutoScroll; set => SetField(ref _ioAutoScroll, value); }

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    // ─── 命令 ──────────────────────────────────────────────────────────────
    public AsyncRelayCommand OpenCardSerialCommand { get; }
    public AsyncRelayCommand OpenOvenSerialCommand { get; }

    public AsyncRelayCommand ConnectPressureCommand { get; }
    public AsyncRelayCommand SetPressureCommand { get; }
    public AsyncRelayCommand VentCommand { get; }
    public AsyncRelayCommand ReadRangeCommand { get; }
    public AsyncRelayCommand ZeroCommand { get; }
    public AsyncRelayCommand ReadStatusCommand { get; }
    public AsyncRelayCommand ReadPressureCommand { get; }
    public AsyncRelayCommand ReadModelCommand { get; }
    public AsyncRelayCommand SwitchModeCommand { get; }
    public AsyncRelayCommand SelfTestCommand { get; }
    public AsyncRelayCommand SendScpiCommand { get; }
    public AsyncRelayCommand ReadScpiReplyCommand { get; }

    public AsyncRelayCommand SetTempCommand { get; }
    public AsyncRelayCommand StopOvenCommand { get; }
    public AsyncRelayCommand ReadOvenCommand { get; }

    public AsyncRelayCommand SwitchVoltageCommand { get; }
    public AsyncRelayCommand SwitchUtPowerCommand { get; }

    public AsyncRelayCommand ReadDriveVCommand { get; }
    public AsyncRelayCommand ReadDriveICommand { get; }
    public AsyncRelayCommand ReadUsigCommand { get; }
    public AsyncRelayCommand ReadUtCommand { get; }
    public AsyncRelayCommand ReadTCommand { get; }
    public AsyncRelayCommand Read40uACommand { get; }

    public AsyncRelayCommand SampleSlotCommand { get; }
    public AsyncRelayCommand SampleAllCommand { get; }

    public RelayCommand ClearDataIoCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public ManualViewModel(TestSession session, DeviceStatusVm? ovenStatus = null, DeviceStatusVm? dacStatus = null)
    {
        _session = session;
        _ovenStatus = ovenStatus;
        _dacStatus = dacStatus;

        foreach (var s in session.Slots.Entries) SlotNames.Add(s.Slot);
        if (SlotNames.Count > 0) _selectedSlot = SlotNames[0];
        ApplySelectedSlotMapping();

        Valves.Add(new ValveVm(this, 0));
        for (int i = 1; i <= 8; i++)
            Valves.Add(new ValveVm(this, i));

        RefreshComPortsCommand = new RelayCommand(_ => RefreshComPorts());
        RefreshComPorts();

        DeviceBus.Traffic += OnTraffic;

        OpenCardSerialCommand = Wrap("打开采集卡串口", async () =>
        {
            IsCardSerialOpen = !IsCardSerialOpen;
            if (!IsCardSerialOpen)
            {
                await _session.Dac.CloseAsync();
                _dacStatus?.SetOverride(ConnectionState.Disconnected);
                DeviceBus.Info("DAQ-Card", $"{CardCom} closed");
                Hist($"采集卡串口 {CardCom} 已关闭");
                return;
            }
            await _session.Dac.OpenAsync();
            _dacStatus?.SetOverride(ConnectionState.Connected);
            Hist($"采集卡串口 {CardCom} 已打开");
        });
        OpenOvenSerialCommand = Wrap("打开烘箱串口", async () =>
        {
            if (IsOvenSerialOpen)
            {
                _ovenSerial?.Close();
                IsOvenSerialOpen = false;
                _ovenStatus?.SetOverride(ConnectionState.Disconnected);
                DeviceBus.Info("Oven", $"{OvenCom} closed");
                Hist($"烘箱串口 {OvenCom} 已关闭");
                return;
            }

            await OpenOvenSerialAsync();
        });

        ConnectPressureCommand = Wrap("联接压力控制器", async () =>
        {
            await session.Pressure.OpenAsync();
            ReadModelText = PressureModel;
            Hist($"已连接 {PressureModel} @ 端口{PressurePort} 地址{PressureAddr}");
        });
        SetPressureCommand = Wrap("加压", async () =>
        {
            await session.Pressure.SetPressureAsync(TargetPressure, PressureUnit, Precision);
            Hist($"加压 → {TargetPressure} {PressureUnit} (精度 {Precision})");
        });
        VentCommand = Wrap("泄压", async () =>
        {
            await session.Pressure.VentAsync();
            Hist("已发起泄压");
        });
        ReadRangeCommand = Wrap("读取量程", async () =>
        {
            var v = await _session.Pressure.ReadUpperLimitAsync();
            ReadRangeText = $"{v} {PressureUnit}";
            Hist($"量程 = {ReadRangeText}");
        });
        ZeroCommand = Wrap("调零", async () =>
        {
            DeviceBus.Tx(PressureModel, "Caldisable=No,Autozero");
            await Task.Delay(20);
            DeviceBus.Rx(PressureModel, "OK");
            Hist("调零完成");
        });
        ReadStatusCommand = Wrap("读取状态", async () =>
        {
            DeviceBus.Tx(PressureModel, "Stable?");
            await Task.Delay(20);
            var ok = new Random().Next(0, 2) == 0;
            DeviceBus.Rx(PressureModel, ok ? "Stable" : "Unstable");
            ReadStatusText = ok ? "稳定" : "波动";
            Hist($"压控状态 = {ReadStatusText}");
        });
        ReadPressureCommand = Wrap("读取压力", async () =>
        {
            var v = await session.Pressure.ReadPressureAsync();
            ReadPressureText = $"{v:F3} {PressureUnit}";
            Hist($"读压 = {ReadPressureText}");
        });
        ReadModelCommand = Wrap("读取型号", async () =>
        {
            DeviceBus.Tx(PressureModel, "*IDN?");
            await Task.Delay(20);
            DeviceBus.Rx(PressureModel, PressureModel);
            ReadModelText = PressureModel;
            Hist($"型号 = {PressureModel}");
        });
        SwitchModeCommand = Wrap("切换测量模式", async () =>
        {
            var next = MeasureMode == "Gauge" ? "Absolute" : "Gauge";
            DeviceBus.Tx(PressureModel, $"Ptype={next}");
            await Task.Delay(20);
            DeviceBus.Rx(PressureModel, "OK");
            MeasureMode = next;
            Hist($"切换测量模式 → {next}");
        });
        SelfTestCommand = Wrap("自检", async () =>
        {
            DeviceBus.Info("Manual", "Self-test all devices");
            await session.Pressure.SelfTestAsync();
            await session.Oven.SelfTestAsync();
            await session.Dmm.SelfTestAsync();
            await session.Dac.SelfTestAsync();
            await session.Power.SelfTestAsync();
            await session.Board.SelfTestAsync();
            Hist("自检完成");
        });
        SendScpiCommand = Wrap("发送指令", async () =>
        {
            if (string.IsNullOrWhiteSpace(ManualScpi)) { Hist("指令为空"); return; }
            DeviceBus.Tx(PressureModel, ManualScpi);
            await Task.Delay(20);
            Hist($"已发送: {ManualScpi}");
        });
        ReadScpiReplyCommand = Wrap("读取返回值", async () =>
        {
            await Task.Delay(20);
            var reply = $"REPLY[{DateTime.Now:HH:mm:ss}] OK";
            DeviceBus.Rx(PressureModel, reply);
            ManualScpiReply = reply;
            Hist($"返回: {reply}");
        });

        SetTempCommand = Wrap("设定温度", async () =>
        {
            var setCommand = $"TEMP,S{TargetTemp.ToString("0.0", CultureInfo.InvariantCulture)}";
            var setReply = await QueryOvenLineAsync(setCommand);
            if (!setReply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("温度设定失败：" + setReply, "烘箱", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var openReply = await QueryOvenLineAsync("POWER,ON");
            if (!openReply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("烘箱启动失败：" + openReply, "烘箱", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Hist($"设温并启动 → {TargetTemp:F1} °C，返回：{setReply} / {openReply}");
        });
        StopOvenCommand = Wrap("停止烘箱", async () =>
        {
            var reply = await QueryOvenLineAsync("POWER,OFF");
            Hist($"停止烘箱，返回：{reply}");
        });
        ReadOvenCommand = Wrap("读取温度", async () =>
        {
            var reply = await QueryOvenLineAsync("TEMP?");
            ReadOvenText = FormatOvenTemperature(reply);
            Hist($"温度 = {ReadOvenText}");
        });

        SwitchVoltageCommand = Wrap("切换电压", async () =>
        {
            // 在 5V / 12V 之间循环：当前显示哪个就切到另一个，输出立即生效。
            var target = string.Equals(CurrentVoltage, "5", StringComparison.OrdinalIgnoreCase) ? 12f : 5f;
            await session.Power.SetVoltageAsync(target);
            await session.Power.OutputOnAsync();
            CurrentVoltage = target.ToString(CultureInfo.InvariantCulture);
            Hist($"切换电压 → {target} V");
        });
        SwitchUtPowerCommand = Wrap("切换UT电源", async () =>
        {
            if (_session.Dmm.State != ConnectionState.Connected)
                await _session.Dmm.OpenAsync();

            var channel = GetSelectedUtPowerChannel();
            var closeSelected = !string.Equals(_closedUtPowerCard, CardAddr, StringComparison.OrdinalIgnoreCase);
            foreach (var ch in GetAllUtPowerChannels())
                await _session.Dmm.OpenRelayAsync(ch);
            if (closeSelected)
                await _session.Dmm.CloseRelayAsync(channel);
            _closedUtPowerCard = closeSelected ? CardAddr : "";
            OnPropertyChanged(nameof(UtPowerText));
            Hist($"切换 UT 电源 → {(closeSelected ? "关闭" : "默认开启")} (采集卡{CardAddr}={channel}，其余板卡默认开启)");
        });

        ReadDriveVCommand = Wrap("读驱动电压", async () =>
            await ReadDacValueAsync(2, "USource", v => ReadDriveV = $"{v:F7}", "V"));
        ReadDriveICommand = Wrap("读驱动电流", async () =>
            await ReadDacValueAsync(3, "ISource", v => ReadDriveI = $"{v:F7}", "mA"));
        ReadUsigCommand = Wrap("读 Usig", async () =>
            await ReadDacValueAsync(4, "USignal", v => ReadUsig = $"{v:F9}", "mV"));
        ReadUtCommand = Wrap("读 UT", async () =>
            await ReadDacValueAsync(5, "UT", v => ReadUT = $"{v:F7}", "mV"));
        ReadTCommand = Wrap("读温度变送器", async () =>
        {
            DeviceBus.Tx("M30-DAC", $"READ_T? addr={CardAddr} ch={ChannelAddr}");
            await Task.Delay(20);
            var v = Math.Round(25.0 + (new Random().NextDouble() - 0.5) * 0.5, 3);
            DeviceBus.Rx("M30-DAC", v.ToString("F3"));
            ReadT = $"{v:F3}";
            Hist($"T = {v:F3} mV");
        });
        Read40uACommand = Wrap("读 40uA", async () =>
        {
            DeviceBus.Tx("M30-DAC", $"READ_40UA? addr={CardAddr} ch={ChannelAddr}");
            await Task.Delay(20);
            var v = Math.Round(40.0 + (new Random().NextDouble() - 0.5) * 0.2, 4);
            DeviceBus.Rx("M30-DAC", v.ToString("F4"));
            Read40uA = $"{v:F4}";
            Hist($"40uA = {v:F4} uA");
        });

        SampleSlotCommand = Wrap("采集 (当前工位)",
            () => SampleAsync(new[] { SelectedSlot }));
        SampleAllCommand  = Wrap("采集 (全部工位)",
            () => SampleAsync(SlotNames.ToArray()));

        ClearDataIoCommand  = new RelayCommand(_ => { DataIo.Clear(); DataIoText = ""; });
        ClearHistoryCommand = new RelayCommand(_ => { History.Clear(); HistoryText = ""; });
    }

    /// <summary>
    /// Convenience wrapper: creates an AsyncRelayCommand that tags exceptions
    /// with the operation name so the global error dialog shows useful titles.
    /// </summary>
    private AsyncRelayCommand Wrap(string source, Func<Task> body) =>
        new(body) { Source = source };

    private Task OpenOvenSerialAsync()
    {
        if (_ovenSerial is { IsOpen: true } &&
            string.Equals(_ovenSerial.PortName, OvenCom, StringComparison.OrdinalIgnoreCase))
        {
            DeviceBus.Info("Oven", $"{OvenCom} already open");
            return Task.CompletedTask;
        }

        _ovenSerial?.Dispose();
        _ovenSerial = new SerialPort(
            OvenCom,
            int.Parse(OvenBaud, CultureInfo.InvariantCulture),
            Enum.Parse<Parity>(OvenParity),
            int.Parse(OvenData, CultureInfo.InvariantCulture),
            OvenStop switch
            {
                "1.5" => System.IO.Ports.StopBits.OnePointFive,
                "2" => System.IO.Ports.StopBits.Two,
                _ => System.IO.Ports.StopBits.One
            })
        {
            NewLine = "\r\n",
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };

        DeviceBus.Tx("Oven", $"OPEN {OvenCom} {OvenBaud} {OvenParity} {OvenData}{OvenStop}");
        _ovenSerial.Open();
        IsOvenSerialOpen = true;
        _ovenStatus?.SetOverride(ConnectionState.Connected);
        DeviceBus.Rx("Oven", "OK");
        Hist($"烘箱串口 {OvenCom} 已打开");
        return Task.CompletedTask;
    }

    private async Task WriteOvenLineAsync(string command)
    {
        await OpenOvenSerialAsync();
        if (_ovenSerial is null || !_ovenSerial.IsOpen)
            throw new InvalidOperationException($"烘箱串口 {OvenCom} 未打开");

        DeviceBus.Tx("Oven", "Send: " + command);
        _ovenSerial.WriteLine(command);
        await Task.Delay(50);
    }

    private async Task<string> QueryOvenLineAsync(string command)
    {
        await OpenOvenSerialAsync();
        if (_ovenSerial is null || !_ovenSerial.IsOpen)
            throw new InvalidOperationException($"烘箱串口 {OvenCom} 未打开");

        _ovenSerial.DiscardInBuffer();
        DeviceBus.Tx("Oven", "Send: " + command);
        var bytes = System.Text.Encoding.ASCII.GetBytes(command);
        _ovenSerial.Write(bytes, 0, bytes.Length);
        await Task.Delay(300);
        var reply = _ovenSerial.BytesToRead > 0 ? _ovenSerial.ReadExisting() : "";
        DeviceBus.Rx("Oven", "Get: " + (string.IsNullOrWhiteSpace(reply) ? "NO RX" : reply.Trim()));
        return reply.Trim();
    }

    public async Task ToggleValveAsync(int index, bool on)
    {
        var channel = GetValveChannel(index);

        if (_session.Dmm.State != ConnectionState.Connected)
            await _session.Dmm.OpenAsync();
        if (on)
            await _session.Dmm.OpenRelayAsync(channel);
        else
            await _session.Dmm.CloseRelayAsync(channel);
        await Task.Delay(GetDelaySettingMs("ValveSwitchMs", 500));
        Hist($"{GetValveLabel(index)}({channel}) → {(on ? "开" : "关")}");
    }

    private static string GetValveLabel(int index) => index == 0 ? "总阀" : $"阀{index}";

    private static string GetValveChannel(int index)
    {
        var ini = LoadSettingIni();
        var key = index == 0 ? "MasterValve" : $"Valve{index}";
        var fallback = index switch
        {
            0 => "101",
            1 => "103",
            2 => "102",
            3 => "104",
            4 => "105",
            5 => "106",
            6 => "107",
            7 => "108",
            8 => "109",
            _ => throw new InvalidOperationException("未知阀门编号")
        };
        return ini.Get("ValveSettings", key, fallback);
    }

    private static int GetDelaySettingMs(string key, int fallback)
    {
        var ini = LoadSettingIni();
        return int.TryParse(ini.Get("DelaySettings", key, fallback.ToString(CultureInfo.InvariantCulture)), out var value)
            ? Math.Max(0, value)
            : fallback;
    }

    private static IniFile LoadSettingIni() =>
        File.Exists(AppPaths.SettingIni) ? IniFile.Load(AppPaths.SettingIni) : new IniFile();

    private string GetSelectedUtPowerChannel()
    {
        if (!int.TryParse(CardAddr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var card) || card < 1 || card > 16)
            card = 1;
        return LoadSettingIni().Get("SwitchUnitCards", $"Card{card}", (300 + card).ToString());
    }

    private static IEnumerable<string> GetAllUtPowerChannels()
    {
        var ini = LoadSettingIni();
        for (var i = 1; i <= 16; i++)
            yield return ini.Get("SwitchUnitCards", $"Card{i}", (300 + i).ToString());
    }

    private static string FormatOvenTemperature(string reply)
    {
        var first = reply.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? reply;
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? $"{t:0.0} °C"
            : first;
    }

    private void ApplySelectedSlotMapping()
    {
        var slot = _session.Slots.Entries.FirstOrDefault(s => s.Slot == SelectedSlot);
        if (slot is null) return;
        if (CardAddrs.Contains(slot.BoardSlotNo)) CardAddr = slot.BoardSlotNo;
        if (ChannelAddrs.Contains(slot.FixtureSlotNo)) ChannelAddr = slot.FixtureSlotNo;
    }

    private async Task ReadDacValueAsync(byte function, string name, Action<float> assign, string unit)
    {
        if (!byte.TryParse(CardAddr, out var card) || !byte.TryParse(ChannelAddr, out var channel))
        {
            MessageBox.Show("请选择采集卡地址和通道地址", "手动采集", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _session.Dac.OpenAsync();
        var value = function switch
        {
            2 => await _session.Dac.ReadUsourceAsync(0, 25, 5, card.ToString(CultureInfo.InvariantCulture), channel.ToString(CultureInfo.InvariantCulture)),
            3 => await _session.Dac.ReadIsourceAsync(0, 25, 5, card.ToString(CultureInfo.InvariantCulture), channel.ToString(CultureInfo.InvariantCulture)),
            4 => await _session.Dac.ReadUsigAsync(0, 25, 5, card.ToString(CultureInfo.InvariantCulture), channel.ToString(CultureInfo.InvariantCulture)),
            5 => await _session.Dac.ReadUtAsync(0, 25, 5, card.ToString(CultureInfo.InvariantCulture), channel.ToString(CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException("未知采集功能码")
        };

        assign(value);
        Hist($"{name}值：{value}");
    }

    private static byte[] BuildCrcFrame(byte card, byte function, byte channel) =>
        AppendCrc(new[] { card, function, channel });

    private static byte[] AppendCrc(byte[] data)
    {
        var crc = ModbusCrc(data);
        var frame = new byte[data.Length + 2];
        Array.Copy(data, frame, data.Length);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private static ushort ModbusCrc(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    private static string ToHex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2"))) + " ";

    private async Task SampleAsync(string[] slotNames)
    {
        var measure = SelectedMeasure;
        var col = $"{ManualLabel}_{measure}";
        var pressure = await _session.Pressure.ReadPressureAsync();
        var temp = await _session.Oven.ReadTempAsync();

        DeviceBus.Info("Manual",
            $"Sample {measure} for {slotNames.Length} slot(s) @ p={pressure:F2}, T={temp:F2}");

        foreach (var slotName in slotNames)
        {
            var slot = _session.Slots.Entries.FirstOrDefault(s => s.Slot == slotName);
            if (slot is null) continue;

            double v;
            try
            {
                v = measure switch
                {
                    "Usig"    => await _session.Dac.ReadUsigAsync   (pressure, temp, 5, slot.BoardSlotNo, slot.FixtureSlotNo),
                    "UT"      => await _session.Dac.ReadUtAsync     (pressure, temp, 5, slot.BoardSlotNo, slot.FixtureSlotNo),
                    "Usource" => await _session.Dac.ReadUsourceAsync(pressure, temp, 5, slot.BoardSlotNo, slot.FixtureSlotNo),
                    "Isource" => await _session.Dac.ReadIsourceAsync(pressure, temp, 5, slot.BoardSlotNo, slot.FixtureSlotNo),
                    "DMM_V"   => await _session.Dmm.ReadVoltageAsync(slot.Channel),
                    "DMM_R"   => await _session.Dmm.ReadResistanceAsync(slot.Channel),
                    _ => double.NaN,
                };
            }
            catch (Exception ex)
            {
                DeviceBus.Info("Manual", $"{slotName} {measure} 失败: {ex.Message}");
                _session.Matrix.Set(slotName, col, double.NaN, CellStatus.Error);
                continue;
            }
            _session.Matrix.Set(slotName, col, v, CellStatus.Ok);
        }
        Hist($"采集 {measure} 完成，{slotNames.Length} 工位 → 列 {col}");
    }

    private void OnTraffic(object? sender, BusEvent e)
    {
        var visible =
            (e.Direction == BusDirection.Tx   && IoShowTx)   ||
            (e.Direction == BusDirection.Rx   && IoShowRx)   ||
            (e.Direction == BusDirection.Info && IoShowInfo);
        if (!visible) return;

        // Render as a single line matching the look of ASLab's "数据I/O" panel.
        var line = $"{e.Time:HH:mm:ss.fff}  {e.Arrow}  {e.Device,-14}  {e.Payload}";
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            DataIo.Add(line);
            while (DataIo.Count > 2000) DataIo.RemoveAt(0);
            DataIoText = string.Join(Environment.NewLine, DataIo);
        }));
    }

    private void Hist(string s)
    {
        StatusText = s;
        AppLog.Info("Manual", s);
        var line = $"{DateTime.Now:HH:mm:ss}  {s}";
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            History.Add(line);
            while (History.Count > 500) History.RemoveAt(0);
            HistoryText = string.Join(Environment.NewLine, History);
        }));
    }
}

/// <summary>
/// One of the 8 valve indicators in 阀门控制. Click toggles state and emits TX.
/// </summary>
public sealed class ValveVm : ViewModelBase
{
    private readonly ManualViewModel _parent;
    private bool _updating;
    public int Index { get; }
    public string Label => Index == 0 ? "总阀" : $"阀{Index}";

    private bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_updating)
            {
                SetField(ref _isOn, value);
                OnPropertyChanged(nameof(StateText));
                return;
            }
            if (!SetField(ref _isOn, value)) return;
            OnPropertyChanged(nameof(StateText));
            _ = ToggleAsync(value);
        }
    }
    public string StateText => _isOn ? "开" : "关";

    public ValveVm(ManualViewModel parent, int index)
    {
        _parent = parent;
        Index = index;
    }

    private async Task ToggleAsync(bool value)
    {
        try
        {
            await _parent.ToggleValveAsync(Index, value);
        }
        catch (Exception ex)
        {
            MessageBox.Show("阀门操作失败：" + ex.Message, Label, MessageBoxButton.OK, MessageBoxImage.Warning);
            _updating = true;
            IsOn = !value;
            _updating = false;
        }
    }
}
