using System;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class LogViewModel : ViewModelBase, IDisposable
{
    private const int MaxEvents = 2000;

    /// <summary>结构化事件缓冲：绑定 <see cref="UiLogBuffer.Text"/> 显示，Flushed 驱动滚动。</summary>
    public UiLogBuffer Buffer { get; } = new(MaxEvents);

    public RelayCommand ClearCommand { get; }

    public LogViewModel()
    {
        ClearCommand = new RelayCommand(_ => Buffer.Clear());
        AppLog.Logged += OnAppLogLogged;
    }

    private void OnAppLogLogged(object? sender, LogEvent e) =>
        Buffer.Post(e.ToString());

    public void Dispose() => AppLog.Logged -= OnAppLogLogged;
}
