using System;
using System.Collections.ObjectModel;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class LogViewModel : ViewModelBase, IDisposable
{
    private const int MaxEvents = 2000;
    private readonly object _logGate = new();
    private readonly Queue<LogEvent> _pendingEvents = new();
    private bool _flushPending;

    public ObservableCollection<LogEvent> Events { get; } = new();

    private string _logText = "";
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    public RelayCommand ClearCommand { get; }

    public LogViewModel()
    {
        ClearCommand = new RelayCommand(_ =>
        {
            lock (_logGate) _pendingEvents.Clear();
            Events.Clear();
            LogText = "";
        });
        AppLog.Logged += OnAppLogLogged;
    }

    private void OnAppLogLogged(object? sender, LogEvent e)
    {
        lock (_logGate)
        {
            _pendingEvents.Enqueue(e);
            if (_flushPending) return;
            _flushPending = true;
        }

        Application.Current.Dispatcher.BeginInvoke(new Action(FlushEvents));
    }

    private void FlushEvents()
    {
        List<LogEvent> events;
        lock (_logGate)
        {
            events = _pendingEvents.ToList();
            _pendingEvents.Clear();
            _flushPending = false;
        }

        foreach (var e in events)
            Events.Add(e);
        while (Events.Count > MaxEvents)
            Events.RemoveAt(0);
        LogText = string.Join(Environment.NewLine, Events.Select(x => x.ToString()));
    }

    public void Dispose() => AppLog.Logged -= OnAppLogLogged;
}
