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

    public ObservableCollection<QuickTestRowVm> Rows { get; } = new();

    private string _status = "就绪";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }

    public string SensorName => string.IsNullOrWhiteSpace(_session.Plan.SensorType) ? _session.Plan.Name : _session.Plan.SensorType;

    private int _startSlot = 1;
    public int StartSlot { get => _startSlot; set => SetField(ref _startSlot, Math.Max(1, value)); }

    private int _endSlot = 8;
    public int EndSlot { get => _endSlot; set => SetField(ref _endSlot, Math.Max(1, value)); }

    private string _p0 = "0";
    public string P0 { get => _p0; set => SetField(ref _p0, value); }

    private string _p50 = "50";
    public string P50 { get => _p50; set => SetField(ref _p50, value); }

    private string _p100 = "100";
    public string P100 { get => _p100; set => SetField(ref _p100, value); }

    private string _pressureUnit = "";
    public string PressureUnit { get => _pressureUnit; set => SetField(ref _pressureUnit, value); }

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
        _spanMin = session.Plan.Specs.Span.Min;
        _spanMax = session.Plan.Specs.Span.Max;
        _linearityMin = session.Plan.Specs.Linearity.Min;
        _linearityMax = session.Plan.Specs.Linearity.Max;
        EndSlot = Math.Min(8, Math.Max(1, session.Slots.Entries.Count));
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
        SpanMin = _session.Plan.Specs.Span.Min;
        SpanMax = _session.Plan.Specs.Span.Max;
        LinearityMin = _session.Plan.Specs.Linearity.Min;
        LinearityMax = _session.Plan.Specs.Linearity.Max;
        RefreshRows();
    }

    private void RefreshRows()
    {
        Rows.Clear();
        var lookup = _session.Slots.Entries.ToDictionary(s => SlotDacAddress.ParseSlotIndex(s.Slot), s => s);
        for (var i = StartSlot; i <= Math.Max(StartSlot, EndSlot); i++)
        {
            if (lookup.TryGetValue(i, out var slot))
                Rows.Add(new QuickTestRowVm { Slot = slot.Slot, SerialNo = slot.SerialNo });
            else
                Rows.Add(new QuickTestRowVm { Slot = $"Slot{i}", SerialNo = "-" });
        }
    }

    private async Task StartAsync()
    {
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

        RefreshRows();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Status = "连接压力控制器";
            await _session.Pressure.OpenAsync(ct);
            await _session.Pressure.SetMeasureAsync(ct);
            await _session.Pressure.SetPressureTypeAsync(_session.Plan.DefaultPressureType, ct);

            await SamplePointAsync("P0", p0, row => row.P0, (row, value) => row.P0 = F(value), ct);
            await SamplePointAsync("P50", p50, row => row.P50, (row, value) => row.P50 = F(value), ct);
            await SamplePointAsync("P100", p100, row => row.P100, (row, value) => row.P100 = F(value), ct);

            Calculate(p0, p50, p100);
            var saved = SaveToDesktop();
            Status = $"完成，已保存到 {saved}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task SamplePointAsync(string pointName, float pressure, Func<QuickTestRowVm, string> _, Action<QuickTestRowVm, double> setValue, CancellationToken ct)
    {
        Status = $"设置压力 {pointName}={pressure:G6} {PressureUnit}";
        await _session.Pressure.SetPressureAsync(pressure, PressureUnit, _session.Plan.Precision, ct);
        await DacBatchSampler.SampleAllAsync(
            _session.Context,
            DacMeasureKind.Usig,
            $"Quick_{pointName}_USG",
            pressure,
            25,
            ct,
            onSlotComplete: (slot, value, ok) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var row = Rows.FirstOrDefault(r => string.Equals(r.Slot, slot.Slot, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) setValue(row, ok ? value : double.NaN);
                });
            },
            startSlot: StartSlot,
            endSlot: EndSlot);
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
    }

    private string SaveToDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var sensor = SafeFileName(SensorName);
        var path = Path.Combine(desktop, $"{DateTime.Now:yyyy-MM-dd HH时mm分ss秒}_{sensor}_快速测试.csv");
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        sw.WriteLine("SensorType,Slot,SerialNo,P0,P50,P100,Usig_P0,Usig_P50,Usig_P100,Span,NL,FailedMetrics,TestResult");
        foreach (var row in Rows)
        {
            sw.WriteLine(string.Join(",", new[]
            {
                Csv(SensorName), Csv(row.Slot), Csv(row.SerialNo), Csv(P0), Csv(P50), Csv(P100),
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _session.Reconfigured -= OnSessionReconfigured;
    }
}
