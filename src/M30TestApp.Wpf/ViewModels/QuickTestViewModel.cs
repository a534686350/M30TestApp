using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Devices;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class QuickTestRowVm : ViewModelBase
{
    public string Slot { get; init; } = "";
    public string SerialNo { get; init; } = "";
    public string Board { get; init; } = "";
    public string BoardSlotNo { get; init; } = "";

    private string _p0 = "";
    public string P0 { get => _p0; set => SetField(ref _p0, value); }

    private string _p50 = "";
    public string P50 { get => _p50; set => SetField(ref _p50, value); }

    private string _p100 = "";
    public string P100 { get => _p100; set => SetField(ref _p100, value); }

    private string _span = "";
    public string Span { get => _span; set => SetField(ref _span, value); }

    private string _linearity = "";
    public string Linearity { get => _linearity; set => SetField(ref _linearity, value); }

    private string _failedMetrics = "";
    public string FailedMetrics { get => _failedMetrics; set => SetField(ref _failedMetrics, value); }

    private string _testResult = "";
    public string TestResult { get => _testResult; set => SetField(ref _testResult, value); }
}

public sealed class QuickTestViewModel : ViewModelBase, IDisposable
{
    private readonly TestSession _session;
    private CancellationTokenSource? _cts;
    private string _appliedPressureKey = "";

    public ObservableCollection<QuickTestRowVm> Rows { get; } = new();
    public ObservableCollection<string> PressurePorts { get; } =
        new(Enumerable.Range(0, 32).Select(i => i.ToString(CultureInfo.InvariantCulture)));
    public ObservableCollection<string> PressureAddrs { get; } =
        new(Enumerable.Range(0, 32).Select(i => i.ToString(CultureInfo.InvariantCulture)));
    public ObservableCollection<string> PressureUnits { get; } = new() { "kPa", "MPa" };
    public ObservableCollection<string> PressureModels { get; } = new();

    private string _status = "就绪";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }

    public string SensorName => string.IsNullOrWhiteSpace(_session.Plan.SensorType) ? _session.Plan.Name : _session.Plan.SensorType;

    private int _startSlot = 1;
    public int StartSlot { get => _startSlot; set => SetField(ref _startSlot, Math.Max(1, value)); }

    private int _endSlot = 8;
    public int EndSlot { get => _endSlot; set => SetField(ref _endSlot, Math.Max(1, value)); }

    private string _p0 = "";
    public string P0 { get => _p0; set => SetField(ref _p0, value); }

    private string _p50 = "";
    public string P50 { get => _p50; set => SetField(ref _p50, value); }

    private string _p100 = "";
    public string P100 { get => _p100; set => SetField(ref _p100, value); }

    private string _pressureUnit = "";
    public string PressureUnit { get => _pressureUnit; set => SetField(ref _pressureUnit, value); }

    private string _pressurePort = "0";
    public string PressurePort { get => _pressurePort; set => SetField(ref _pressurePort, value); }

    private string _pressureAddr = "0";
    public string PressureAddr { get => _pressureAddr; set => SetField(ref _pressureAddr, value); }

    private string _pressureModel = "";
    public string PressureModel { get => _pressureModel; set => SetField(ref _pressureModel, value); }

    private string _holdSeconds = "5";
    public string HoldSeconds { get => _holdSeconds; set => SetField(ref _holdSeconds, value); }

    private string _pressurePrecision = "0.05";
    public string PressurePrecision { get => _pressurePrecision; set => SetField(ref _pressurePrecision, value); }

    private string _spanMin = "";
    public string SpanMin { get => _spanMin; set => SetField(ref _spanMin, value); }

    private string _spanMax = "";
    public string SpanMax { get => _spanMax; set => SetField(ref _spanMax, value); }

    private string _linearityMin = "";
    public string LinearityMin { get => _linearityMin; set => SetField(ref _linearityMin, value); }

    private string _linearityMax = "";
    public string LinearityMax { get => _linearityMax; set => SetField(ref _linearityMax, value); }

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshSlotsCommand { get; }

    public QuickTestViewModel(TestSession session)
    {
        _session = session;
        _pressureUnit = string.IsNullOrWhiteSpace(session.Plan.PressureUnit) ? "kPa" : session.Plan.PressureUnit;
        _spanMin = "60";
        _spanMax = "140";
        _linearityMin = "-2";
        _linearityMax = "2";
        _pressurePrecision = session.Plan.Precision.ToString(CultureInfo.InvariantCulture);
        _holdSeconds = LoadPressureHoldSeconds(session.Context.Settings).ToString(CultureInfo.InvariantCulture);
        EndSlot = Math.Max(1, session.Slots.Entries.Count);
        LoadPressureOptions();
        LoadPressureProfileFromSession();
        RefreshRows();

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning) { Source = "QuickTest" };
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        RefreshSlotsCommand = new RelayCommand(_ => RefreshRows(), _ => !IsRunning);

        session.Reconfigured += OnSessionReconfigured;
    }

    private void OnSessionReconfigured(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SensorName));
        PressureUnit = string.IsNullOrWhiteSpace(_session.Plan.PressureUnit) ? PressureUnit : _session.Plan.PressureUnit;
        SpanMin = "60";
        SpanMax = "140";
        LinearityMin = "-2";
        LinearityMax = "2";
        PressurePrecision = _session.Plan.Precision.ToString(CultureInfo.InvariantCulture);
        HoldSeconds = LoadPressureHoldSeconds(_session.Context.Settings).ToString(CultureInfo.InvariantCulture);
        LoadPressureOptions();
        LoadPressureProfileFromSession();
        RefreshRows();
    }

    private void RefreshRows()
    {
        Rows.Clear();
        var lookup = _session.Slots.Entries.ToDictionary(s => SlotDacAddress.ParseSlotIndex(s.Slot), s => s);
        for (var i = StartSlot; i <= Math.Max(StartSlot, EndSlot); i++)
        {
            if (lookup.TryGetValue(i, out var slot))
                Rows.Add(new QuickTestRowVm { Slot = slot.Slot, SerialNo = slot.SerialNo, Board = slot.Board, BoardSlotNo = slot.BoardSlotNo });
            else
            {
                var board = ((i - 1) / 16) + 1;
                var boardSlotNo = ((i - 1) % 16) + 1;
                Rows.Add(new QuickTestRowVm { Slot = $"Slot{i}", SerialNo = $"DEMO_{i:D3}", Board = board.ToString(), BoardSlotNo = boardSlotNo.ToString() });
            }
        }
    }

    private void LoadPressureOptions()
    {
        PressureModels.Clear();
        foreach (var model in _session.Commands.Models
                     .Where(m => _session.Commands.Has(m, "SetPressure") || _session.Commands.Has(m, "ReadPressure"))
                     .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            PressureModels.Add(model);
        }

        foreach (var fallback in new[] { "FLUKE-7250", "FLUKE-6270", "WIKA-CPC8000" })
        {
            if (!PressureModels.Contains(fallback))
                PressureModels.Add(fallback);
        }
    }

    private void LoadPressureProfileFromSession()
    {
        var pressure = _session.Station.Get(DeviceKind.Pressure);
        if (pressure is null) return;

        if (!string.IsNullOrWhiteSpace(pressure.Model))
            PressureModel = pressure.Model;
        ParseGpibAddress(pressure.Address, out var port, out var address);
        PressurePort = port;
        PressureAddr = address;
    }

    private void ApplyPressureProfileToSession()
    {
        var existing = _session.Station.Get(DeviceKind.Pressure);
        var model = string.IsNullOrWhiteSpace(PressureModel) ? existing?.Model ?? "FLUKE-7250" : PressureModel.Trim();
        var port = string.IsNullOrWhiteSpace(PressurePort) ? "0" : PressurePort.Trim();
        var address = string.IsNullOrWhiteSpace(PressureAddr) ? "0" : PressureAddr.Trim();
        var resource = BuildGpibAddress(port, address);
        var key = $"{model}|{resource}|{existing?.Backend}|{existing?.Baud}|{existing?.Parity}|{existing?.DataBits}|{existing?.StopBits}";

        if (string.Equals(_appliedPressureKey, key, StringComparison.Ordinal))
            return;

        _session.Station.Devices[DeviceKind.Pressure] = new DeviceProfile
        {
            Kind = DeviceKind.Pressure,
            Model = model,
            Backend = existing?.Backend ?? DeviceBackend.Hw,
            Address = resource,
            Baud = existing?.Baud ?? 9600,
            Parity = existing?.Parity ?? "N",
            DataBits = existing?.DataBits ?? 8,
            StopBits = existing?.StopBits ?? "1"
        };
        _session.RebuildDevices(_session.DebugMode);
        _appliedPressureKey = key;
        Log($"压力控制器配置已应用：{model} @ {resource}");
    }

    private static void ParseGpibAddress(string resource, out string port, out string address)
    {
        port = "0";
        address = "0";
        if (string.IsNullOrWhiteSpace(resource)) return;
        if (!resource.StartsWith("GPIB", StringComparison.OrdinalIgnoreCase)) return;

        var parts = resource.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        port = parts[0].Length > 4 ? parts[0][4..] : "0";
        address = parts[1];
    }

    private static string BuildGpibAddress(string port, string address) => $"GPIB{port}::{address}::INSTR";

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(P0) || string.IsNullOrWhiteSpace(P50) || string.IsNullOrWhiteSpace(P100))
        {
            Status = "请先填写 P0 / P50 / P100 压力点值";
            Log("请先填写 P0 / P50 / P100 压力点值");
            return;
        }
        if (!TryFloat(P0, out var p0) || !TryFloat(P50, out var p50) || !TryFloat(P100, out var p100))
        {
            Status = "压力点输入无效";
            return;
        }
        if (p100 <= p0)
        {
            Status = "P100 必须大于 P0";
            return;
        }
        if (!int.TryParse(HoldSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var holdSeconds) &&
            !int.TryParse(HoldSeconds, NumberStyles.Integer, CultureInfo.CurrentCulture, out holdSeconds))
        {
            Log("保压时间输入无效");
            return;
        }
        holdSeconds = Math.Max(0, holdSeconds);
        if (!TryFloat(PressurePrecision, out var pressurePrecision) || pressurePrecision <= 0)
        {
            Log("压力精度输入无效");
            return;
        }

        RefreshRows();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Log("连接压力控制器");
            ApplyPressureProfileToSession();
            await _session.Pressure.OpenAsync(ct);
            await _session.Pressure.SetMeasureAsync(ct);
            await _session.Pressure.SetPressureTypeAsync(_session.Plan.DefaultPressureType, ct);
            Log($"压力模式：{_session.Plan.DefaultPressureType}");

            await SamplePointAsync("P0", p0, holdSeconds, pressurePrecision, row => row.P0, (row, value) => row.P0 = F(value), ct);
            await SamplePointAsync("P50", p50, holdSeconds, pressurePrecision, row => row.P50, (row, value) => row.P50 = F(value), ct);
            await SamplePointAsync("P100", p100, holdSeconds, pressurePrecision, row => row.P100, (row, value) => row.P100 = F(value), ct);

            Log("计算 Span / NL");
            Calculate(p0, p50, p100);
            var saved = SaveToDesktop();
            Log($"完成，已保存到 {saved}");
        }
        catch (OperationCanceledException)
        {
            Log("快速测试已停止");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task SamplePointAsync(string pointName, float pressure, int holdSeconds, float pressurePrecision, Func<QuickTestRowVm, string> _, Action<QuickTestRowVm, double> setValue, CancellationToken ct)
    {
        Log($"设置压力 {pointName}={pressure:G6} {PressureUnit}，精度 {pressurePrecision:G6}");
        await SetAndWaitPressureAsync(pointName, pressure, pressurePrecision, ct);
        if (holdSeconds > 0)
        {
            for (var remaining = holdSeconds; remaining > 0; remaining--)
            {
                Status = $"{pointName} 保压 {remaining}s";
                await Task.Delay(1000, ct);
            }
            Log($"{pointName} 保压完成");
        }

        Log($"{pointName} 开始采集 Usig，工位 {StartSlot}-{EndSlot}");
        var okCount = 0;
        var failCount = 0;
        await DacBatchSampler.SampleAllAsync(
            _session.Context,
            DacMeasureKind.Usig,
            $"Quick_{pointName}_USG",
            pressure,
            25,
            ct,
            onSlotComplete: (slot, value, ok) =>
            {
                if (ok) okCount++; else failCount++;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var row = Rows.FirstOrDefault(r => string.Equals(r.Slot, slot.Slot, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) setValue(row, ok ? value : double.NaN);
                });
            },
            startSlot: StartSlot,
            endSlot: EndSlot);
        Log($"{pointName} 采集完成：成功 {okCount}，失败 {failCount}");
    }

    private async Task SetAndWaitPressureAsync(string pointName, float pressure, float pressurePrecision, CancellationToken ct)
    {
        await _session.Pressure.SetPressureTypeAsync(_session.Plan.DefaultPressureType, ct);
        Log($"压力类型切换为 {_session.Plan.DefaultPressureType}");
        await _session.Pressure.SetPressureAsync(pressure, PressureUnit, pressurePrecision, ct);

        for (var i = 0; i < 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            var current = await _session.Pressure.ReadPressureAsync(ct);
            var diff = Math.Abs(current - pressure);
            Status = $"{pointName} 目标{pressure:G6} 当前{current:F4} 差值{diff:F4} {PressureUnit}";
            if (i % 10 == 0)
                Log($"等待压力稳定：目标 {pressure:G6}，当前 {current:F4}，差值 {diff:F4} ({i}/120)");
            if (diff <= pressurePrecision)
            {
                Log($"压力已稳定：{current:F4}{PressureUnit}（目标 {pressure:G6}，精度 {pressurePrecision:G6}）");
                return;
            }
            await Task.Delay(500, ct);
        }

        Log($"{pointName} 压力稳定等待超时，继续采集");
    }

    private void Calculate(float p0, float p50, float p100)
    {
        foreach (var row in Rows)
        {
            if (!TryDouble(row.P0, out var u0) || !TryDouble(row.P50, out var u50) || !TryDouble(row.P100, out var u100))
            {
                row.Span = "";
                row.Linearity = "";
                row.FailedMetrics = "USIG";
                row.TestResult = "fail";
                continue;
            }

            var span = u100 - u0;
            var ratio = (p50 - p0) / (p100 - p0);
            var ideal = u0 + span * ratio;
            var nl = Math.Abs(span) < 1e-12 ? double.NaN : (u50 - ideal) / span * 100.0;
            row.Span = F(span);
            row.Linearity = F(nl);

            var failed = new List<string>();
            if (!InRange(span, SpanMin, SpanMax)) failed.Add("Span");
            if (!InRange(nl, LinearityMin, LinearityMax)) failed.Add("NL");
            row.FailedMetrics = string.Join(" ", failed);
            row.TestResult = failed.Count == 0 ? "pass" : "fail";
        }

        var pass = Rows.Count(r => string.Equals(r.TestResult, "pass", StringComparison.OrdinalIgnoreCase));
        var fail = Rows.Count(r => string.Equals(r.TestResult, "fail", StringComparison.OrdinalIgnoreCase));
        Log($"计算完成：Pass {pass}，Fail {fail}");
    }

    private string SaveToDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var sensor = SafeFileName(SensorName);
        var path = Path.Combine(desktop, $"{DateTime.Now:yyyy-MM-dd HH时mm分ss秒}_{sensor}_快速测试.csv");
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        sw.WriteLine("SensorType,Slot,SerialNo,Board,BoardSlotNo,P0,P50,P100,Usig_P0,Usig_P50,Usig_P100,Span,NL,FailedMetrics,TestResult");
        foreach (var row in Rows)
        {
            sw.WriteLine(string.Join(",", new[]
            {
                Csv(SensorName), Csv(row.Slot), Csv(row.SerialNo), Csv(row.Board), Csv(row.BoardSlotNo), Csv(P0), Csv(P50), Csv(P100),
                Csv(row.P0), Csv(row.P50), Csv(row.P100), Csv(row.Span), Csv(row.Linearity),
                Csv(row.FailedMetrics), Csv(row.TestResult)
            }));
        }
        return path;
    }

    private static bool InRange(double value, string minText, string maxText)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        if (TryDouble(minText, out var min) && value < min) return false;
        if (TryDouble(maxText, out var max) && value > max) return false;
        return true;
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static string F(double value) => double.IsNaN(value) ? "" : value.ToString("G6", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string SafeFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "QuickUsig" : safe;
    }

    private static int LoadPressureHoldSeconds(IniFile settings)
    {
        var text = settings.Get("DelaySettings", "PressureAfterMs", "60000");
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? Math.Max(0, ms / 1000)
            : 60;
    }

    private void Log(string message)
    {
        Status = message;
        AppLog.Info("QuickTest", message);
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        var lines = (LogText.Length == 0 ? Enumerable.Empty<string>() : LogText.Split(Environment.NewLine))
            .Concat(new[] { line })
            .TakeLast(300);
        LogText = string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _session.Reconfigured -= OnSessionReconfigured;
    }
}
