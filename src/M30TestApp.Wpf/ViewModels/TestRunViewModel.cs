using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using TestCheckpoint = M30TestApp.Core.Data.TestCheckpoint;
using M30TestApp.Core.TaskScript.Actions;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class TestRunViewModel : ViewModelBase, IDisposable
{
    private static TestRunViewModel? s_activeRunPage;
    private readonly TestSession _session;
    private readonly bool _isLongTermStabilityMode;
    private LongTermStabilityMeasureMode _longTermMeasureMode = LongTermStabilityMeasureMode.Voltage;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _pauseTcs;
    private bool _isPaused;
    private string _status = "就绪";
    private string _currentStep = "—";
    private int _progressIndex;
    private int _progressTotal;
    private int _currentTempIndex;
    private int _totalTempPoints;
    private double _okCount, _warnCount, _errCount;
    private string _lastError = "";
    private const int MaxLogLines = 500;
    private readonly Dictionary<string, CellStatus> _rowStatusBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    public ObservableCollection<MatrixRowVm> Rows { get; } = new();
    public ObservableCollection<MatrixColumnVm> MatrixColumns { get; } = new();
    public ObservableCollection<LongTermMatrixColumnVm> LongTermColumns { get; } = new();

    /// <summary>运行日志缓冲：绑定其 <see cref="UiLogBuffer.Text"/>，Flushed 驱动自动滚动。</summary>
    public UiLogBuffer Log { get; } = new(MaxLogLines, trimChunk: 100);

    public string Status { get => _status; set => SetField(ref _status, value); }
    public string CurrentStep { get => _currentStep; set => SetField(ref _currentStep, value); }
    public int ProgressIndex { get => _progressIndex; set { if (SetField(ref _progressIndex, value)) OnPropertyChanged(nameof(ProgressPercentValue)); } }
    public int ProgressTotal { get => _progressTotal; set { if (SetField(ref _progressTotal, value)) OnPropertyChanged(nameof(ProgressPercentValue)); } }
    public double ProgressPercentValue => ProgressTotal <= 0 ? 0 : Math.Clamp((double)ProgressIndex / ProgressTotal * 100.0, 0, 100);
    public int CurrentTempIndex { get => _currentTempIndex; private set { if (SetField(ref _currentTempIndex, value)) OnPropertyChanged(nameof(TempPointProgressText)); } }
    public int TotalTempPoints { get => _totalTempPoints; private set { if (SetField(ref _totalTempPoints, value)) OnPropertyChanged(nameof(TempPointProgressText)); } }
    public string TempPointProgressText => $"{CurrentTempIndex} / {TotalTempPoints}";
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
    public string RunModeTitle => _isLongTermStabilityMode ? "长期稳定性测试" : "自动测试";
    public string MatrixTitle => _isLongTermStabilityMode
        ? LongTermStabilityMatrix.MatrixTitle(_longTermMeasureMode)
        : "采集数据矩阵";
    public bool IsLongTermStabilityMode => _isLongTermStabilityMode;
    public int TotalSlots => Rows.Count;

    /// <summary>是否正在跑（站点上报要判断状态）。首次运行前 RunAsync 会先弹"选择运行方案"，
    /// 那时 _cts 已经建好但还没真正开始 —— 这里只作为"忙闲"的粗略判断。</summary>
    public bool IsRunning => _cts is not null;

    // ─── 启动续测（程序一打开就问，见 MainViewModel.PromptPendingResumeOnStartup） ───
    //
    // 启动时已经弹过「检测到未完成的测试」并拿到保温时长，走正常开始流程时不要再问第二遍。

    /// <summary>由启动续测流程预置的保温时长（分钟）。</summary>
    public int? PendingResumeSoakMinutes { get; set; }

    /// <summary>true 时本次 Run 跳过「检测到未完成的测试」弹窗，直接按预置值续测。</summary>
    public bool SkipResumePrompt { get; set; }

    private string _currentT = "—";
    public string CurrentT { get => _currentT; set => SetField(ref _currentT, value); }
    private string _currentP = "—";
    public string CurrentP { get => _currentP; set => SetField(ref _currentP, value); }

    private DateTime? _startedAt;
    private string _elapsed = "00:00:00";
    public string Elapsed { get => _elapsed; set => SetField(ref _elapsed, value); }

    /// <summary>本次运行开始时间（没在跑为 null）。总控制台详情页要显示"什么时候开的"。</summary>
    public DateTime? StartedAt => _startedAt;

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

    public TestRunViewModel(TestSession session, bool isLongTermStabilityMode = false)
    {
        _session = session;
        _isLongTermStabilityMode = isLongTermStabilityMode;

        _longTermMeasureMode = session.Context.LongTermMeasureMode;
        TotalTempPoints = session.Plan.TempPoints.Count;

        foreach (var slot in session.Slots.Entries)
            Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo, slot));

        if (_isLongTermStabilityMode)
            ApplyLongTermColumns();

        session.Matrix.CellUpdated += OnCellUpdated;
        session.Runner.Progress += OnProgress;
        session.Reconfigured += OnReconfigured;

        AppLog.Logged += OnAppLogLogged;

        RunCommand = new AsyncRelayCommand(RunAsync, () => _cts is null)
        {
            Source = _isLongTermStabilityMode ? "长期稳定性测试" : "自动测试"
        };
        PauseCommand = new RelayCommand(_ => TogglePause(), _ => _cts is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is not null);
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        // 1-sec timer to refresh Elapsed, CurrentT/P, and CanExecute while running.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_startedAt is { } t)
            {
                Elapsed = FormatElapsed(DateTime.Now - t);
                CurrentT = string.IsNullOrEmpty(_session.Context.CurrentTemp) ? "—" : _session.Context.CurrentTemp;
                CurrentP = string.IsNullOrEmpty(_session.Context.CurrentPressure) ? "—" : _session.Context.CurrentPressure;
            }
            RefreshTemperatureProgress();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        _timer.Start();

        // 33ms 矩阵单元格合批刷新定时器
        _cellFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CellFlushMs) };
        _cellFlushTimer.Tick += FlushCellUpdates;
    }

    private bool IsActiveRunPage => ReferenceEquals(s_activeRunPage, this);

    private void ClaimRunPage() => s_activeRunPage = this;

    /// <summary>
    /// 实时日志只显示"时间 + 采集数据"，剔除设备指令 / 流程 / 保存 / 校准等噪声行。
    /// 数据行形态（见 Core 各采集实现）：
    ///   Slot225 T3P0_USG_R=0.61117 (卡15 通1)          —— DacBatchSampler
    ///   Slot1 SN=xxx CH=1 T1_UT=43.8537                —— BuiltinActions
    ///   长期稳定性：T1-P0 Slot1 SN=xxx CH=1 = 43.8537 Ω —— LongTermStabilityRunner
    ///   T1 烘箱实际温度：24.1℃
    /// </summary>
    private static readonly Regex DataLineRegex = new(
        @"^(?:长期稳定性：\S+\s+)?(?:Slot\d+\s+(?:SN=\S+\s+CH=\S+\s+)?\S+=|T\d+\s+烘箱实际温度：)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>烘箱保温流程行内嵌的实测温度，如 "目标温度为 85°C;  烘箱已加热 1 min; 当前温度为 29.9°C"。</summary>
    private static readonly Regex OvenTempRegex = new(
        @"当前温度为\s*([\d.+-]+)\s*°C",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool TryExtractDataLine(LogEvent e, out string data)
    {
        data = "";
        var msg = e.Message;
        if (string.IsNullOrEmpty(msg)) return false;

        if (DataLineRegex.IsMatch(msg)) { data = msg; return true; }

        var mt = OvenTempRegex.Match(msg);
        if (mt.Success) { data = $"烘箱温度 {mt.Groups[1].Value}°C"; return true; }

        return false;
    }

    private void OnAppLogLogged(object? sender, LogEvent e)
    {
        if (!IsActiveRunPage) return;
        if (!TryExtractDataLine(e, out var data)) return;
        QueueLogLine($"{e.Time:HH:mm:ss.fff}  {data}");
    }

    private void OnProgress(object? sender, Core.TaskScript.TaskProgress e)
    {
        if (!IsActiveRunPage) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            CurrentStep = $"[{e.Index + 1}/{e.Total}] {e.Command}";
            ProgressIndex = e.Index + 1;
            ProgressTotal = e.Total;
            if (e.Phase == "Error")
                LastError = $"{e.Command}: {e.Error}";
            RefreshTemperatureProgress();
            CurrentT = string.IsNullOrEmpty(_session.Context.CurrentTemp) ? "—" : _session.Context.CurrentTemp;
            CurrentP = string.IsNullOrEmpty(_session.Context.CurrentPressure) ? "—" : _session.Context.CurrentPressure;
            if (e.Phase == "Error") Status = "错误: " + e.Error;
        }));
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalHours = (long)Math.Floor(elapsed.TotalHours);
        return $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void RefreshTemperatureProgress()
    {
        TotalTempPoints = _session.Plan.TempPoints.Count;
        CurrentTempIndex = ResolveCurrentTempIndex(_session.Context.CurrentTemp);
    }

    private int ResolveCurrentTempIndex(string currentTemp)
    {
        if (string.IsNullOrWhiteSpace(currentTemp)) return 0;

        for (var i = 0; i < _session.Plan.TempPoints.Count; i++)
        {
            var name = _session.Plan.TempPoints[i].Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!currentTemp.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (currentTemp.Length == name.Length || !char.IsLetterOrDigit(currentTemp[name.Length]))
                return i + 1;
        }

        return 0;
    }

    // ─── 矩阵单元格合批刷新 ─────────────────────────────────────────────────
    // 历史实现：每个 CellUpdated 一次 Dispatcher.BeginInvoke + 列存在性线性扫描，
    // 256 工位高频采集时 dispatcher 队列被灌满。现在入队 + 33ms 定时 drain。
    private const int CellFlushMs = 33;
    private readonly object _cellQueueGate = new();
    private readonly List<CellUpdate> _pendingCellUpdates = new();
    private readonly HashSet<string> _matrixColumnKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _cellFlushTimer;

    private void OnCellUpdated(object? sender, CellUpdate update)
    {
        if (!IsActiveRunPage) return;
        lock (_cellQueueGate)
        {
            _pendingCellUpdates.Add(update);
            if (!_cellFlushTimer.IsEnabled) _cellFlushTimer.Start();
        }
    }

    private void FlushCellUpdates(object? sender, EventArgs e)
    {
        List<CellUpdate> batch;
        lock (_cellQueueGate)
        {
            batch = new List<CellUpdate>(_pendingCellUpdates);
            _pendingCellUpdates.Clear();
            _cellFlushTimer.Stop();
        }
        if (batch.Count == 0) return;

        EnsureRowMap();
        var touched = new List<MatrixRowVm>();
        var seen = new HashSet<MatrixRowVm>();
        foreach (var update in batch)
        {
            if (!_rowBySlot!.TryGetValue(update.Slot, out var row)) continue;
            // 先静默写入，全部格子落盘后再统一发通知：
            // WPF 只认 "Item[]"，一发就是整行重估，故每行每批只发一次，避免逐格刷新的 churn。
            row.Cells.SetDeferred(update.Cell.Key, update.Cell);
            EnsureMatrixColumn(update.Cell.Key);

            // 同一行只做一次状态重算
            if (seen.Add(row))
            {
                ApplyRowStatus(row);
                touched.Add(row);
            }
        }

        foreach (var row in touched)
            row.Cells.NotifyChanged();
    }

    private System.Collections.Generic.Dictionary<string, MatrixRowVm>? _rowBySlot;
    private void EnsureRowMap()
    {
        if (_rowBySlot is not null) return;
        _rowBySlot = new System.Collections.Generic.Dictionary<string, MatrixRowVm>();
        foreach (var r in Rows) _rowBySlot[r.Slot] = r;
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
            MatrixColumns.Clear();
            LongTermColumns.Clear();
            _matrixColumnKeys.Clear();
            _rowStatusBySlot.Clear();
            _rowBySlot = null;
            foreach (var slot in _session.Slots.Entries)
                Rows.Add(new MatrixRowVm(slot.Slot, slot.SerialNo, slot));
            if (_isLongTermStabilityMode)
            {
                _longTermMeasureMode = _session.Context.LongTermMeasureMode;
                ApplyLongTermColumns();
            }

            if (!IsActiveRunPage) return;

            OkCount = WarnCount = ErrCount = 0;
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
            OnPropertyChanged(nameof(RunModeTitle));
            OnPropertyChanged(nameof(MatrixTitle));
        }));
    }

    private void ApplyLongTermColumns()
    {
        LongTermStabilityMatrixSync.Apply(LongTermColumns, _session.Plan, _longTermMeasureMode);
        OnPropertyChanged(nameof(MatrixTitle));
    }

    private void EnsureMatrixColumn(string columnKey)
    {
        if (_isLongTermStabilityMode) return;
        if (!_matrixColumnKeys.Add(columnKey)) return; // HashSet 判重，替代 Any() 线性扫描
        MatrixColumns.Add(new MatrixColumnVm(columnKey, columnKey));
    }

    private void ClearLog()
    {
        Log.Clear();
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

    private async Task<bool> ConfirmLeakCheckExceededAsync(string message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operation = Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                Application.Current.MainWindow,
                message,
                "探漏超限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes);
        return await operation.Task.WaitAsync(ct);
    }

    private async Task<bool> ConfirmPressureInstabilityAsync(string message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operation = Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                Application.Current.MainWindow,
                message,
                "压力波动",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes);
        return await operation.Task.WaitAsync(ct);
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

    private void QueueLogLine(string line) => Log.Post(line);

    private async Task ShutdownGracefullyAsync(bool saveData)
    {
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
                await RunShutdownStepAsync("关闭烘箱", ct => _session.Context.Oven.StopAsync(ct));
            }
        }
        catch (Exception ex) { AppLog.Error("Run", $"关闭烘箱失败: {ex.Message}"); }

        try
        {
            if (_session.Context.Pressure is not null)
            {
                AppLog.Info("Run", "泄压…");
                await RunShutdownStepAsync("泄压", ct => _session.Context.Pressure.VentAsync(ct));
            }
        }
        catch (Exception ex) { AppLog.Error("Run", $"泄压失败: {ex.Message}"); }

    }

    private static async Task RunShutdownStepAsync(string name, Func<CancellationToken, Task> action)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = Task.Run(async () => await action(timeoutCts.Token).ConfigureAwait(false));
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (completed != task)
        {
            timeoutCts.Cancel();
            AppLog.Warn("Run", $"{name}超时，已跳过");
            return;
        }

        await task.ConfigureAwait(false);
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

        return ShowResumeCheckpointDialog(ck);
    }

    /// <summary>
    /// 「检测到未完成的测试」对话框。程序启动时和点开始测试时共用同一个 ——
    /// 文案和行为必须一致，否则同一个断点在两处问出来的话不一样，现场会懵。
    /// </summary>
    public static (bool Cancel, bool? Resume, int? ResumeSoakMinutes) ShowResumeCheckpointDialog(TestCheckpoint ck)
    {
        var title = new TextBlock
        {
            Text = "检测到未完成的测试",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        // 断点粒度是**温度点**：上次停在 T2 的 P3，续测从 T2 的 P0 重测，T2 下旧数据作废。
        var tempNo = ck.TempIndex + 1;
        var pressureText = ck.PressureIndex <= 0
            ? "该温度点还没开始测压力点"
            : $"已测到该温度点的第 {ck.PressureIndex} 个压力点";

        var summary = new TextBlock
        {
            Text = $"方案：{ck.PlanName}\n"
                 + $"上次测到：温度点 T{tempNo}（{pressureText}）\n"
                 + $"保存时间：{ck.SavedAt:yyyy-MM-dd HH:mm}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var keptText = ck.TempIndex switch
        {
            <= 0 => "本次没有更早的温度点。",
            1 => "T1 的数据保留。",
            _ => $"T1 ~ T{ck.TempIndex} 的数据保留。",
        };

        var rule = new TextBlock
        {
            Text = $"续测规则：从温度点 T{tempNo} 的第一个压力点重新测，"
                 + $"T{tempNo} 下已测的数据作废；{keptText}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14)
        };
        rule.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var prompt = new TextBlock
        {
            Text = $"请输入 T{tempNo} 重新测试前的保温时长：",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var minutesBox = new TextBox
        {
            Text = "0",
            Width = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var unit = new TextBlock { Text = "分钟", VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var inputRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
            Children = { minutesBox, unit }
        };

        var hint = new TextBlock
        {
            Text = "填 0 表示不额外保温，直接重新测该温度点。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 18)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var resumeButton = new Button { Content = "继续测试", Width = 96, MinHeight = 34, IsDefault = true };
        resumeButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var restartButton = new Button { Content = "重新开始", Width = 96, MinHeight = 34, Margin = new Thickness(8, 0, 0, 0) };
        restartButton.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        var cancelButton = new Button { Content = "取消", Width = 82, MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { resumeButton, restartButton, cancelButton }
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(22),
            Children = { title, summary, rule, prompt, inputRow, hint, buttons }
        };
        panel.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");

        var result = (Cancel: true, Resume: (bool?)null, ResumeSoakMinutes: (int?)null);
        var window = new Window
        {
            Title = "未完成测试",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Content = panel
        };
        window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");

        resumeButton.Click += (_, _) =>
        {
            var minutes = int.TryParse(minutesBox.Text, out var value) ? Math.Max(0, value) : 0;
            result = (false, true, minutes);
            window.Close();
        };
        restartButton.Click += (_, _) => { result = (false, false, null); window.Close(); };
        cancelButton.Click += (_, _) => window.Close();
        window.Loaded += (_, _) => { minutesBox.Focus(); minutesBox.SelectAll(); };
        window.ShowDialog();

        return result;
    }

    // ─── 测试完成提示窗 ─────────────────────────────────────────────────────
    //
    // 现场跑完一轮最关心三件事：跑完没有、什么时候开始的、一共用了多久。
    // 状态栏那行小字不够醒目（人可能已经去干别的了），所以给一个带起止时间、
    // 用时和工位统计的完成弹窗，并把数据目录直接摆出来。

    /// <summary>把时长说成人话：40 秒 / 3 分 20 秒 / 2 小时 5 分。</summary>
    private static string HumanDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} 小时 {d.Minutes} 分 {d.Seconds} 秒";
        if (d.TotalMinutes >= 1) return $"{d.Minutes} 分 {d.Seconds} 秒";
        return $"{Math.Max(0, d.TotalSeconds):F0} 秒";
    }

    private static Border StatusChip(string text, string brushKey)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        var border = new Border
        {
            Child = tb,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
        };
        border.SetResourceReference(Border.BorderBrushProperty, brushKey);
        return border;
    }

    /// <summary>测试跑完后的完成提示窗（自动测试 / 长期稳定性共用）。</summary>
    internal void ShowRunFinishedDialog(DateTime startedAt, DateTime finishedAt)
    {
        var durationText = HumanDuration(finishedAt - startedAt);

        var title = new TextBlock
        {
            Text = "✔ 测试完成",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");

        var subtitle = new TextBlock
        {
            Text = _isLongTermStabilityMode ? "长期稳定性测试已跑完，设备已关闭。" : "自动测试已跑完，设备已关闭。",
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14),
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var rows = new (string Key, string Value, bool Strong)[]
        {
            ("测试方案", PlanName ?? "-", false),
            ("传感器型号", SensorType ?? "-", false),
            ("启动时间", startedAt.ToString("yyyy-MM-dd HH:mm:ss"), false),
            ("完成时间", finishedAt.ToString("yyyy-MM-dd HH:mm:ss"), false),
            ("测试用时", durationText, true),
            ("数据目录", AppPaths.DataDir, false),
        };

        var kv = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++) kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < rows.Length; i++)
        {
            var key = new TextBlock
            {
                Text = rows[i].Key,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
            };
            key.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(key, i);
            kv.Children.Add(key);

            var value = new TextBlock
            {
                Text = rows[i].Value,
                FontSize = rows[i].Strong ? 15.5 : 13,
                FontWeight = rows[i].Strong ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, rows[i].Strong ? "AccentBrush" : "TextBrush");
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            kv.Children.Add(value);
        }

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16),
            Children =
            {
                StatusChip($"工位 {TotalSlots}", "MutedBrush"),
                StatusChip($"合格 {OkCount:F0}", "SuccessBrush"),
                StatusChip($"警告 {WarnCount:F0}", "WarnBrush"),
                StatusChip($"异常 {ErrCount:F0}", "ErrorBrush"),
            },
        };

        var openButton = new Button
        {
            Content = "打开数据目录",
            MinHeight = 34,
            Padding = new Thickness(14, 0, 14, 0),
        };
        openButton.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

        var okButton = new Button
        {
            Content = "确定",
            Width = 96,
            MinHeight = 34,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            IsCancel = true,
        };
        okButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { openButton, okButton },
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Children = { title, subtitle, kv, chips, buttons },
        };
        panel.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");

        var window = new Window
        {
            Title = "测试完成",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            Content = panel,
            ShowInTaskbar = true,   // 人可能切去干别的了，任务栏要能看到
        };
        window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");

        openButton.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("UI", "打开数据目录失败：" + ex.Message); }
        };
        okButton.Click += (_, _) => window.Close();
        window.Loaded += (_, _) => okButton.Focus();

        window.ShowDialog();
        AppLog.Info("Run", $"测试完成弹窗已确认：{startedAt:HH:mm:ss} → {finishedAt:HH:mm:ss}，用时 {durationText}");
    }

    private async Task RunAsync()
    {
        ClaimRunPage();

        // 先弹出"选择运行方案 + 工位"对话框；取消则直接放弃本次运行。
        (bool Cancel, bool? Resume, int? ResumeSoakMinutes) resumePrompt;
        if (SkipResumePrompt)
        {
            // 程序启动时已经问过「是否继续 + 保温多久」，这里不再重复打扰。
            SkipResumePrompt = false;
            resumePrompt = (false, true, PendingResumeSoakMinutes);
            PendingResumeSoakMinutes = null;
        }
        else
        {
            resumePrompt = PromptResumeCheckpoint();
        }

        if (resumePrompt.Cancel)
        {
            Status = "已取消（未启动）";
            return;
        }

        var setupVm = new RunSetupViewModel(_session, _isLongTermStabilityMode);
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

        if (_isLongTermStabilityMode)
        {
            _longTermMeasureMode = setupVm.LongTermMeasureMode;
            _session.Context.LongTermMeasureMode = setupVm.LongTermMeasureMode;
            ApplyLongTermColumns();
        }

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
        var savedLeakCheckNote = _session.Context.LeakCheckNote;
        var savedPauseWaiter = _session.Context.PauseWaiter;
        var savedConfirmLeakCheckExceededAsync = _session.Context.ConfirmLeakCheckExceededAsync;
        var savedConfirmPressureInstabilityAsync = _session.Context.ConfirmPressureInstabilityAsync;
        var savedResumeSoakMinutesOverride = _session.Context.ResumeSoakMinutesOverride;
        var savedTaskScript = _session.Plan.TaskScript;

        _session.Context.PauseWaiter = WaitIfPausedAsync;
        _session.Context.ConfirmLeakCheckExceededAsync = ConfirmLeakCheckExceededAsync;
        _session.Context.ConfirmPressureInstabilityAsync = ConfirmPressureInstabilityAsync;
        _session.Context.ResumeSoakMinutesOverride = setupVm.ResumeFromCheckpoint ? resumePrompt.ResumeSoakMinutes : null;
        _session.Context.UseVentForGaugeZeroPressure = setupVm.UseVentForGaugeZeroPressure;

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
            _session.Context.LeakCheckNote = "探漏已跳过";
            AppLog.Info("Run", "已跳过探漏");
        }
        AppLog.Info("Run", setupVm.UseVentForGaugeZeroPressure
            ? "0kPa 表压将使用排放方式"
            : "0kPa 表压将使用正常控压方式");

        // Set data acquisition skip flags
        _session.Context.SkipUt = _isLongTermStabilityMode || !setupVm.CollectUt;
        _session.Context.SkipUsc = _isLongTermStabilityMode || !setupVm.CollectUsc;
        _session.Context.SkipIsc = _isLongTermStabilityMode || !setupVm.CollectIsc;
        _session.Context.SkipUsg = _isLongTermStabilityMode || !setupVm.CollectUsg;
        _session.Context.SkipOvenTemp = _isLongTermStabilityMode || !setupVm.CollectOvenTemp;

        // 万用表采集模式：电压 / 电阻 由本轮勾选决定（烘箱温度复用上面的 SkipOvenTemp）
        var isDmmRun = !_isLongTermStabilityMode && setupVm.UseDmmAutoTest;
        _session.Context.SkipDmmVoltage = isDmmRun && !setupVm.DmmCollectVoltage;
        _session.Context.SkipDmmResistance = isDmmRun && !setupVm.DmmCollectResistance;

        if (_isLongTermStabilityMode)
        {
            _session.Plan.TaskScript = "Run:LongTermStabilityTest";
            _session.Context.Plan = _session.Plan;
            var measureLabel = setupVm.LongTermMeasureMode == LongTermStabilityMeasureMode.Resistance ? "电阻" : "电压";
            AppLog.Info("Run", $"测试模式：长期稳定性测试，DAQ973A 采集{measureLabel}");
        }
        else
        {
            // 运行设置中的模式选择必须真正切换执行脚本。
            // 之前这里只在长期稳定性模式赋值，导致 DMM 自动测试界面选中后
            // 实际仍可能执行方案文件里的 Run:PerformanceTest。
            _session.Plan.TaskScript = setupVm.RunTaskScript;
            if (!setupVm.CollectUt) AppLog.Info("Run", "已跳过UT采集");
            if (!setupVm.CollectUsc) AppLog.Info("Run", "已跳过USC采集");
            if (!setupVm.CollectIsc) AppLog.Info("Run", "已跳过ISC采集");
            if (!setupVm.CollectUsg) AppLog.Info("Run", "已跳过USG采集");
            if (!setupVm.CollectOvenTemp) AppLog.Info("Run", "已跳过烘箱温度采集");
        }

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
            // 跑完给个明显的完成弹窗：起止时间 / 用时 / 工位统计 / 数据目录
            ShowRunFinishedDialog(_startedAt ?? DateTime.Now, DateTime.Now);
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
            _session.Context.LeakCheckNote = savedLeakCheckNote;
            _session.Context.PauseWaiter = savedPauseWaiter;
            _session.Context.ConfirmLeakCheckExceededAsync = savedConfirmLeakCheckExceededAsync;
            _session.Context.ConfirmPressureInstabilityAsync = savedConfirmPressureInstabilityAsync;
            _session.Context.ResumeSoakMinutesOverride = savedResumeSoakMinutesOverride;
            _session.Plan.TaskScript = savedTaskScript;
            _session.Context.Plan = _session.Plan;
            IsPaused = false;
            _pauseTcs?.TrySetResult();
            _pauseTcs = null;
            _cts?.Dispose();
            _cts = null;
            _startedAt = null;
            try { await AppLog.FlushAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { AppLog.Error("Log", $"等待日志写入完成失败：{ex}"); }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _cellFlushTimer.Stop();
        if (ReferenceEquals(s_activeRunPage, this)) s_activeRunPage = null; // 释放静态根，避免 VM 被持有到进程结束
        AppLog.Logged -= OnAppLogLogged;
        _session.Matrix.CellUpdated -= OnCellUpdated;
        _session.Runner.Progress -= OnProgress;
        _session.Reconfigured -= OnReconfigured;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
