using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Data;
using TestCheckpoint = M30TestApp.Core.Data.TestCheckpoint;
using M30TestApp.Core.TaskScript.Actions;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class TestRunViewModel : ViewModelBase, IDisposable
{
    private readonly TestSession _session;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _pauseTcs;
    private bool _isPaused;
    private string _status = "就绪";
    private string _currentStep = "—";
    private int _progressIndex;
    private int _progressTotal;
    private double _okCount, _warnCount, _errCount;
    private string _lastError = "";
    private const int MaxLogLines = 500;
    private readonly object _logGate = new();
    private readonly Queue<string> _pendingLogLines = new();
    private readonly Dictionary<string, CellStatus> _rowStatusBySlot = new(StringComparer.OrdinalIgnoreCase);
    private bool _logFlushPending;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<MatrixRowVm> Rows { get; } = new();
    public ObservableCollection<string> Columns { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private string _logText = "";
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    public string Status { get => _status; set => SetField(ref _status, value); }
    public string CurrentStep { get => _currentStep; set => SetField(ref _currentStep, value); }
    public int ProgressIndex { get => _progressIndex; set { if (SetField(ref _progressIndex, value)) OnPropertyChanged(nameof(ProgressPercentValue)); } }
    public int ProgressTotal { get => _progressTotal; set { if (SetField(ref _progressTotal, value)) OnPropertyChanged(nameof(ProgressPercentValue)); } }
    public double ProgressPercentValue => ProgressTotal <= 0 ? 0 : Math.Clamp((double)ProgressIndex / ProgressTotal * 100.0, 0, 100);
    public double OkCount { get => _okCount; set { if (SetField(ref _okCount, value)) OnPropertyChanged(nameof(OkPercent)); } }
    public double WarnCount { get => _warnCount; set { if (SetField(ref _warnCount, value)) OnPropertyChanged(nameof(WarnPercent)); } }
    public double ErrCount { get => _errCount; set { if (SetField(ref _errCount, value)) OnPropertyChanged(nameof(ErrPercent)); } }
    public double Total => Math.Max(1, TotalSlots);
    public string OkPercent => $"{OkCount / Total * 100:F1}%";
    public string WarnPercent => $"{WarnCount / Total * 100:F1}%";
    public string ErrPercent => $"{ErrCount / Total * 100:F1}%";
    public string LastError { get => _lastError; private set => SetField(ref _lastError, value); }

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
    public RelayCommand PauseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetField(ref _isPaused, value))
                OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    public TestRunViewModel(TestSession session)
    {
        _session = session;

        foreach (var slot in session.Slots.Entries)
            Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo));

        session.Matrix.CellUpdated += OnCellUpdated;
        session.Runner.Progress += OnProgress;
        session.Reconfigured += OnReconfigured;

        AppLog.Logged += OnAppLogLogged;

        RunCommand = new AsyncRelayCommand(RunAsync, () => _cts is null) { Source = "测试" };
        PauseCommand = new RelayCommand(_ => TogglePause(), _ => _cts is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is not null);
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        // 1-sec timer to refresh Elapsed, CurrentT/P, and CanExecute while running.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_startedAt is { } t)
            {
                Elapsed = (DateTime.Now - t).ToString(@"hh\:mm\:ss");
                CurrentT = string.IsNullOrEmpty(_session.Context.CurrentTemp) ? "—" : _session.Context.CurrentTemp;
                CurrentP = string.IsNullOrEmpty(_session.Context.CurrentPressure) ? "—" : _session.Context.CurrentPressure;
            }
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        _timer.Start();
    }

    private void OnAppLogLogged(object? sender, LogEvent e) => QueueLogLine(e.ToString());

    private void OnProgress(object? sender, Core.TaskScript.TaskProgress e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            CurrentStep = $"[{e.Index + 1}/{e.Total}] {e.Command}";
            ProgressIndex = e.Index + 1;
            ProgressTotal = e.Total;
            if (e.Phase == "Error")
                LastError = $"{e.Command}: {e.Error}";
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
                ApplyRowStatus(row);
            }
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

    private void ApplyRowStatus(MatrixRowVm row)
    {
        var oldStatus = _rowStatusBySlot.TryGetValue(row.Slot, out var old) ? old : CellStatus.Empty;
        var newStatus = GetRowStatus(row);
        if (oldStatus == newStatus) return;

        AdjustStatusCount(oldStatus, -1);
        AdjustStatusCount(newStatus, 1);

        if (newStatus == CellStatus.Empty)
            _rowStatusBySlot.Remove(row.Slot);
        else
            _rowStatusBySlot[row.Slot] = newStatus;
    }

    private void AdjustStatusCount(CellStatus status, int delta)
    {
        switch (status)
        {
            case CellStatus.Ok:
                OkCount = Math.Max(0, OkCount + delta);
                break;
            case CellStatus.Warn:
                WarnCount = Math.Max(0, WarnCount + delta);
                break;
            case CellStatus.Error:
                ErrCount = Math.Max(0, ErrCount + delta);
                break;
        }
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
            _rowStatusBySlot.Clear();
            _rowBySlot = null;
            foreach (var slot in _session.Slots.Entries)
                Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo));
            OkCount = WarnCount = ErrCount = 0;
            _rowStatusBySlot.Clear();
            LastError = "";
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
        lock (_logGate) _pendingLogLines.Clear();
        LogLines.Clear();
        LogText = "";
        LastError = "";
        CurrentStep = "—";
        ProgressIndex = 0;
        ProgressTotal = 0;
    }

    private Task WaitIfPausedAsync(CancellationToken ct)
    {
        if (!IsPaused) return Task.CompletedTask;
        var tcs = _pauseTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return tcs.Task.WaitAsync(ct);
    }

    private void TogglePause()
    {
        if (_cts is null) return;

        if (!IsPaused)
        {
            _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IsPaused = true;
            Status = "已暂停";
            AppLog.Info("Run", "测试已暂停");
        }
        else
        {
            IsPaused = false;
            _pauseTcs?.TrySetResult();
            _pauseTcs = null;
            Status = "运行中";
            AppLog.Info("Run", "测试继续");
        }

        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void QueueLogLine(string line)
    {
        lock (_logGate)
        {
            _pendingLogLines.Enqueue(line);
            if (_logFlushPending) return;
            _logFlushPending = true;
        }

        Application.Current.Dispatcher.BeginInvoke(new Action(FlushLogLines));
    }

    private void FlushLogLines()
    {
        List<string> lines;
        lock (_logGate)
        {
            lines = _pendingLogLines.ToList();
            _pendingLogLines.Clear();
            _logFlushPending = false;
        }

        foreach (var line in lines)
            LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
        LogText = string.Join(Environment.NewLine, LogLines);
    }

    private async Task ShutdownGracefullyAsync(bool saveData)
    {
        var noCt = CancellationToken.None;
        if (saveData)
        {
            try
            {
                AppLog.Info("Run", "保存测试数据…");
                RunPerformanceTestAction.SaveMatrix(_session.Context);
            }
            catch (Exception ex) { AppLog.Error("Run", $"保存数据失败: {ex.Message}"); }
        }

        try
        {
            if (_session.Context.Oven is not null)
            {
                AppLog.Info("Run", "关闭烘箱…");
                await _session.Context.Oven.StopAsync(noCt);
            }
        }
        catch (Exception ex) { AppLog.Error("Run", $"关闭烘箱失败: {ex.Message}"); }

        try
        {
            if (_session.Context.Pressure is not null)
            {
                AppLog.Info("Run", "泄压…");
                await _session.Context.Pressure.VentAsync(noCt);
            }
        }
        catch (Exception ex) { AppLog.Error("Run", $"泄压失败: {ex.Message}"); }

        try
        {
            if (_session.Context.Power is not null)
            {
                AppLog.Info("Run", "关闭电源…");
                await _session.Context.Power.OutputOffAsync(noCt);
            }
        }
        catch (Exception ex) { AppLog.Error("Run", $"关闭电源失败: {ex.Message}"); }
    }

    private int GetRunnerMaxConsecutiveErrors()
    {
        var text = _session.Context.Settings.Get("Run", "MaxConsecutiveErrors", "1");
        return int.TryParse(text, out var value) ? Math.Max(1, value) : 1;
    }

    private (bool Cancel, bool? Resume, int? ResumeSoakMinutes) PromptResumeCheckpoint()
    {
        if (!AppPreferences.SaveCheckpointOnAbort(_session.Context.Settings) || !TestCheckpoint.Exists())
            return (false, null, null);

        var ck = TestCheckpoint.Load();
        if (ck is null)
            return (false, null, null);

        var result = MessageBox.Show(
            $"检测到上次有未完成的测试。\n\n方案：{ck.PlanName}\n温度点：{ck.TempIndex + 1}\n压力点：{ck.PressureIndex + 1}\n保存时间：{ck.SavedAt:yyyy-MM-dd HH:mm}\n\n是否继续上次测试？\n选择“否”将从头开始新测试。",
            "未完成测试",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return (true, null, null);

        if (result == MessageBoxResult.No)
            return (false, false, null);

        var text = Microsoft.VisualBasic.Interaction.InputBox(
            "请输入续测时当前温度点需要继续保温的时间（分钟）。\n填 0 表示不额外保温，直接继续后续步骤。",
            "续测保温时间",
            "0");

        var minutes = int.TryParse(text, out var value) ? Math.Max(0, value) : 0;
        return (false, true, minutes);
    }

    private async Task RunAsync()
    {
        // 先弹出"选择运行方案 + 工位"对话框；取消则直接放弃本次运行。
        var resumePrompt = PromptResumeCheckpoint();
        if (resumePrompt.Cancel)
        {
            Status = "已取消（未启动）";
            return;
        }

        var setupVm = new RunSetupViewModel(_session);
        if (resumePrompt.Resume.HasValue)
            setupVm.ResumeFromCheckpoint = resumePrompt.Resume.Value && setupVm.CanResumeCheckpoint;
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

        ClearLog();

        _cts = new CancellationTokenSource();
        _startedAt = DateTime.Now;

        var savedPressure = _session.Context.Pressure;
        var savedOven = _session.Context.Oven;
        var savedSkipLeak = _session.Context.SkipLeakCheck;
        var savedSkipUt = _session.Context.SkipUt;
        var savedSkipUsc = _session.Context.SkipUsc;
        var savedSkipIsc = _session.Context.SkipIsc;
        var savedSkipUsg = _session.Context.SkipUsg;
        var savedSkipOvenTemp = _session.Context.SkipOvenTemp;
        var savedPauseWaiter = _session.Context.PauseWaiter;
        var savedResumeSoakMinutesOverride = _session.Context.ResumeSoakMinutesOverride;

        _session.Context.PauseWaiter = WaitIfPausedAsync;
        _session.Context.ResumeSoakMinutesOverride = setupVm.ResumeFromCheckpoint ? resumePrompt.ResumeSoakMinutes : null;

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

        // Set data acquisition skip flags
        _session.Context.SkipUt = !setupVm.CollectUt;
        _session.Context.SkipUsc = !setupVm.CollectUsc;
        _session.Context.SkipIsc = !setupVm.CollectIsc;
        _session.Context.SkipUsg = !setupVm.CollectUsg;
        _session.Context.SkipOvenTemp = !setupVm.CollectOvenTemp;

        if (!setupVm.CollectUt) AppLog.Info("Run", "已跳过UT采集");
        if (!setupVm.CollectUsc) AppLog.Info("Run", "已跳过USC采集");
        if (!setupVm.CollectIsc) AppLog.Info("Run", "已跳过ISC采集");
        if (!setupVm.CollectUsg) AppLog.Info("Run", "已跳过USG采集");
        if (!setupVm.CollectOvenTemp) AppLog.Info("Run", "已跳过烘箱温度采集");

        _session.Runner.ThrowOnUnknown = true;
        _session.Runner.MaxConsecutiveErrors = GetRunnerMaxConsecutiveErrors();
        _session.Context.ResumeCheckpoint = null;
        if (setupVm.ResumeFromCheckpoint && setupVm.CanResumeCheckpoint)
        {
            var ck = TestCheckpoint.Load();
            if (ck is not null && ck.MatchesPlan(_session.Plan.Name))
            {
                _session.Context.ResumeCheckpoint = ck;
                AppLog.Info("Run", $"续测：{setupVm.CheckpointSummary}");
            }
        }

        try
        {
            Status = "运行中…";
            OkCount = WarnCount = ErrCount = 0;
            _rowStatusBySlot.Clear();
            LastError = "";
            await _session.RunAsync(_cts.Token);
            Status = "正在关闭设备…";
            await ShutdownGracefullyAsync(saveData: false);
            Status = "完成";
        }
        catch (OperationCanceledException)
        {
            Status = "正在保存并关闭设备…";
            await ShutdownGracefullyAsync(saveData: true);
            Status = AppPreferences.SaveCheckpointOnAbort(_session.Context.Settings)
                ? "已取消（数据已保存，可续测）"
                : "已取消（数据已保存）";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "失败: " + ex.Message;
            AppLog.Error("UI", ex.ToString());
            try { await ShutdownGracefullyAsync(saveData: true); }
            catch (Exception ex2) { AppLog.Error("Run", $"异常后关闭设备失败: {ex2.Message}"); }
        }
        finally
        {
            _session.Context.Pressure = savedPressure;
            _session.Context.Oven = savedOven;
            _session.Context.SkipLeakCheck = savedSkipLeak;
            _session.Context.SkipUt = savedSkipUt;
            _session.Context.SkipUsc = savedSkipUsc;
            _session.Context.SkipIsc = savedSkipIsc;
            _session.Context.SkipUsg = savedSkipUsg;
            _session.Context.SkipOvenTemp = savedSkipOvenTemp;
            _session.Context.PauseWaiter = savedPauseWaiter;
            _session.Context.ResumeSoakMinutesOverride = savedResumeSoakMinutesOverride;
            IsPaused = false;
            _pauseTcs?.TrySetResult();
            _pauseTcs = null;
            _cts?.Dispose();
            _cts = null;
            _startedAt = null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        AppLog.Logged -= OnAppLogLogged;
        _session.Matrix.CellUpdated -= OnCellUpdated;
        _session.Runner.Progress -= OnProgress;
        _session.Reconfigured -= OnReconfigured;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
