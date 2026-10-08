using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class LogViewModel : ViewModelBase, IDisposable
{
    private const int MaxEvents = 2000;

    private readonly object _gate = new();
    private readonly List<LogEvent> _events = new();

    /// <summary>结构化事件缓冲：绑定 <see cref="UiLogBuffer.Text"/> 显示，Flushed 驱动滚动。</summary>
    public UiLogBuffer Buffer { get; } = new(MaxEvents);

    private string _filter = "";

    /// <summary>关键字过滤（不区分大小写，匹配时间 / 级别 / 来源 / 正文）；留空显示全部。</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetField(ref _filter, value ?? "")) return;
            Rebuild();
        }
    }

    /// <summary>顶部计数：过滤生效时显示「命中 / 总数」。</summary>
    public string CountText
    {
        get
        {
            lock (_gate)
            {
                if (_filter.Length == 0) return $"{_events.Count} 条";
                return $"{_events.Count(e => Matches(e, _filter))} / {_events.Count} 条";
            }
        }
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenLogDirCommand { get; }
    public RelayCommand OpenDataDirCommand { get; }

    public LogViewModel()
    {
        ClearCommand = new RelayCommand(_ =>
        {
            lock (_gate) _events.Clear();
            Buffer.Clear();
            OnPropertyChanged(nameof(CountText));
        });
        OpenLogDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.LogDir));
        OpenDataDirCommand = new RelayCommand(_ => OpenDirectory(AppPaths.DataDir));
        AppLog.Logged += OnAppLogLogged;
    }

    private void OnAppLogLogged(object? sender, LogEvent e)
    {
        lock (_gate)
        {
            _events.Add(e);
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        if (_filter.Length == 0 || Matches(e, _filter))
            Buffer.Post(e.ToString());
    }

    /// <summary>过滤条件变化后按保留的历史事件重建可见缓冲。</summary>
    private void Rebuild()
    {
        List<LogEvent> snapshot;
        lock (_gate) snapshot = new List<LogEvent>(_events);

        Buffer.Clear();
        foreach (var e in snapshot)
        {
            if (_filter.Length == 0 || Matches(e, _filter))
                Buffer.Post(e.ToString());
        }
        OnPropertyChanged(nameof(CountText));
    }

    private static bool Matches(LogEvent e, string filter) =>
        e.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static void OpenDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Log", $"打开目录失败 {dir}: {ex.Message}");
        }
    }

    public void Dispose() => AppLog.Logged -= OnAppLogLogged;
}
