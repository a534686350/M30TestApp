using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Data;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class TestRunViewModel : ViewModelBase
{
    private readonly TestSession _session;
    private CancellationTokenSource? _cts;
    private string _status = "就绪";
    private string _currentStep = "—";
    private int _progressIndex;
    private int _progressTotal;
    private double _okCount, _warnCount, _errCount;

    public ObservableCollection<MatrixRowVm> Rows { get; } = new();
    public ObservableCollection<string> Columns { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private string _logText = "";
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    public string Status { get => _status; set => SetField(ref _status, value); }
    public string CurrentStep { get => _currentStep; set => SetField(ref _currentStep, value); }
    public int ProgressIndex { get => _progressIndex; set => SetField(ref _progressIndex, value); }
    public int ProgressTotal { get => _progressTotal; set => SetField(ref _progressTotal, value); }
    public double OkCount { get => _okCount; set { if (SetField(ref _okCount, value)) OnPropertyChanged(nameof(OkPercent)); } }
    public double WarnCount { get => _warnCount; set { if (SetField(ref _warnCount, value)) OnPropertyChanged(nameof(WarnPercent)); } }
    public double ErrCount { get => _errCount; set { if (SetField(ref _errCount, value)) OnPropertyChanged(nameof(ErrPercent)); } }
    public double Total => Math.Max(1, TotalSlots);
    public string OkPercent => $"{OkCount / Total * 100:F1}%";
    public string WarnPercent => $"{WarnCount / Total * 100:F1}%";
    public string ErrPercent => $"{ErrCount / Total * 100:F1}%";

    public string PlanName => _session.Plan.Name;
    public string SensorType => _session.Plan.SensorType;
    public int TotalSlots => Rows.Count;

    private string _currentT = "—";
    public string CurrentT { get => _currentT; set => SetField(ref _currentT, value); }
    private string _currentP = "—";
    public string CurrentP { get => _currentP; set => SetField(ref _currentP, value); }

    private DateTime? _startedAt;
    private string _elapsed = "00:00:00";
    public string Elapsed { get => _elapsed; set => SetField(ref _elapsed, value); }

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public TestRunViewModel(TestSession session)
    {
        _session = session;

        foreach (var slot in session.Slots.Entries)
            Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo));

        session.Matrix.CellUpdated += OnCellUpdated;
        session.Runner.Progress += OnProgress;
        session.Reconfigured += OnReconfigured;

        AppLog.Logged += (_, e) =>
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                LogLines.Add(e.ToString());
                while (LogLines.Count > 500) LogLines.RemoveAt(0);
                LogText = string.Join(Environment.NewLine, LogLines);
            }));
        };

        RunCommand = new AsyncRelayCommand(RunAsync, () => _cts is null) { Source = "测试" };
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is not null);
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        // 1-sec timer to refresh Elapsed, CurrentT/P, and CanExecute while running.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_startedAt is { } t)
            {
                Elapsed = (DateTime.Now - t).ToString(@"hh\:mm\:ss");
                CurrentT = string.IsNullOrEmpty(_session.Context.CurrentTemp) ? "—" : _session.Context.CurrentTemp;
                CurrentP = string.IsNullOrEmpty(_session.Context.CurrentPressure) ? "—" : _session.Context.CurrentPressure;
            }
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        timer.Start();
    }

    private void OnProgress(object? sender, Core.TaskScript.TaskProgress e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            CurrentStep = $"[{e.Index + 1}/{e.Total}] {e.Command}";
            ProgressIndex = e.Index + 1;
            ProgressTotal = e.Total;
            CurrentT = string.IsNullOrEmpty(_session.Context.CurrentTemp) ? "—" : _session.Context.CurrentTemp;
            CurrentP = string.IsNullOrEmpty(_session.Context.CurrentPressure) ? "—" : _session.Context.CurrentPressure;
            if (e.Phase == "Error") Status = "错误: " + e.Error;
        }));
    }

    private void OnCellUpdated(object? sender, CellUpdate update)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            EnsureRowMap();
            if (_rowBySlot!.TryGetValue(update.Slot, out var row))
            {
                row.Cells[update.Cell.Key] = update.Cell;
                if (!Columns.Contains(update.Cell.Key)) Columns.Add(update.Cell.Key);
            }
            RecalculateCounts();
        }));
    }

    private System.Collections.Generic.Dictionary<string, MatrixRowVm>? _rowBySlot;
    private void EnsureRowMap()
    {
        if (_rowBySlot is not null) return;
        _rowBySlot = new System.Collections.Generic.Dictionary<string, MatrixRowVm>();
        foreach (var r in Rows) _rowBySlot[r.Slot] = r;
    }

    private void RecalculateCounts()
    {
        var ok = 0;
        var warn = 0;
        var err = 0;
        foreach (var row in Rows)
        {
            var status = GetRowStatus(row);
            switch (status)
            {
                case CellStatus.Ok:
                    ok++;
                    break;
                case CellStatus.Warn:
                    warn++;
                    break;
                case CellStatus.Error:
                    err++;
                    break;
            }
        }

        OkCount = ok;
        WarnCount = warn;
        ErrCount = err;
    }

    private static CellStatus GetRowStatus(MatrixRowVm row)
    {
        var hasOk = false;
        var hasWarn = false;
        foreach (var pair in row.Cells)
        {
            var cell = pair.Value;
            if (cell is null) continue;
            if (cell.Status == CellStatus.Error) return CellStatus.Error;
            if (cell.Status == CellStatus.Warn) hasWarn = true;
            else if (cell.Status == CellStatus.Ok) hasOk = true;
        }

        if (hasWarn) return CellStatus.Warn;
        if (hasOk) return CellStatus.Ok;
        return CellStatus.Empty;
    }

    /// <summary>
    /// Triggered when <see cref="TestSession.ApplyRunConfig"/> swaps in a new plan/slots
    /// for the upcoming run. Rebuilds the bound row set and resets counters so the matrix
    /// matches the freshly chosen slot table.
    /// </summary>
    private void OnReconfigured(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            Rows.Clear();
            Columns.Clear();
            _rowBySlot = null;
            foreach (var slot in _session.Slots.Entries)
                Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo));
            OkCount = WarnCount = ErrCount = 0;
            CurrentT = "—";
            CurrentP = "—";
            CurrentStep = "—";
            Status = "已配置";
            OnPropertyChanged(nameof(TotalSlots));
            OnPropertyChanged(nameof(OkPercent));
            OnPropertyChanged(nameof(WarnPercent));
            OnPropertyChanged(nameof(ErrPercent));
            OnPropertyChanged(nameof(PlanName));
            OnPropertyChanged(nameof(SensorType));
        }));
    }

    private void ClearLog()
    {
        LogLines.Clear();
        LogText = "";
        CurrentStep = "—";
        ProgressIndex = 0;
        ProgressTotal = 0;
    }

    private async Task RunAsync()
    {
        // 先弹出"选择运行方案 + 工位"对话框；取消则直接放弃本次运行。
        var setupVm = new RunSetupViewModel(_session);
        var dlg = new Views.RunSetupWindow(setupVm)
        {
            Owner = Application.Current.MainWindow,
        };
        var ok = dlg.ShowDialog();
        if (ok != true)
        {
            Status = "已取消（未启动）";
            return;
        }

        _cts = new CancellationTokenSource();
        _startedAt = DateTime.Now;

        var savedPressure = _session.Context.Pressure;
        var savedOven = _session.Context.Oven;
        var savedSkipLeak = _session.Context.SkipLeakCheck;
        if (!setupVm.UsePressure)
        {
            _session.Context.Pressure = null;
            AppLog.Info("Run", "已跳过压力控制器");
        }
        if (!setupVm.UseOven)
        {
            _session.Context.Oven = null;
            AppLog.Info("Run", "已跳过烘箱");
        }
        if (!setupVm.UseLeakCheck)
        {
            _session.Context.SkipLeakCheck = true;
            AppLog.Info("Run", "已跳过探漏");
        }

        try
        {
            ClearLog();
            Status = "运行中…";
            OkCount = WarnCount = ErrCount = 0;
            await _session.RunAsync(_cts.Token);
            Status = "完成";
        }
        catch (OperationCanceledException) { Status = "已取消"; }
        catch (Exception ex) { Status = "失败: " + ex.Message; AppLog.Error("UI", ex.ToString()); }
        finally
        {
            _session.Context.Pressure = savedPressure;
            _session.Context.Oven = savedOven;
            _session.Context.SkipLeakCheck = savedSkipLeak;
            _cts?.Dispose();
            _cts = null;
            _startedAt = null;
        }
    }
}
