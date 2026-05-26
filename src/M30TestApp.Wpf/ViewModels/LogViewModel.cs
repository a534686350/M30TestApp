using System;
using System.Collections.ObjectModel;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

public sealed class LogViewModel : ViewModelBase
{
    public ObservableCollection<LogEvent> Events { get; } = new();

    private string _logText = "";
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    public RelayCommand ClearCommand { get; }

    public LogViewModel()
    {
        ClearCommand = new RelayCommand(_ => { Events.Clear(); LogText = ""; });
        AppLog.Logged += (_, e) =>
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Events.Add(e);
                while (Events.Count > 2000) Events.RemoveAt(0);
                LogText = string.Join(Environment.NewLine, Events.Select(x => x.ToString()));
            }));
        };
    }
}
