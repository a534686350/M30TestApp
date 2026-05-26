using System;
using System.Windows.Media;
using M30TestApp.Core.Devices;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>Top-bar device status indicator: name + colored dot + model.</summary>
public sealed class DeviceStatusVm : ViewModelBase
{
    private readonly IDevice _device;
    private ConnectionState _state;
    private ConnectionState? _overrideState;

    public DeviceStatusVm(string title, IDevice device)
    {
        Title = title;
        _device = device;
        _state = device.State;
        device.StateChanged += (_, _) =>
        {
            _state = device.State;
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(DotBrush));
            }));
        };
    }

    public string Title { get; }
    public string Model => string.IsNullOrEmpty(_device.Model) ? "—" : _device.Model;
    public string Address => string.IsNullOrEmpty(_device.Address) ? "" : _device.Address;
    public ConnectionState State => _overrideState ?? _state;

    public string StateText => State switch
    {
        ConnectionState.Connected    => "在线",
        ConnectionState.Connecting   => "连接中",
        ConnectionState.Disconnected => "离线",
        ConnectionState.Faulted      => "故障",
        _ => "—"
    };

    public Brush DotBrush => State switch
    {
        ConnectionState.Connected    => (Brush)App.Current.Resources["SuccessBrush"],
        ConnectionState.Connecting   => (Brush)App.Current.Resources["WarnBrush"],
        ConnectionState.Faulted      => (Brush)App.Current.Resources["ErrorBrush"],
        _ => (Brush)App.Current.Resources["MutedBrush"],
    };

    public void SetOverride(ConnectionState? state)
    {
        _overrideState = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DotBrush));
    }
}
