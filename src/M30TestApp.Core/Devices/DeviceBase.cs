using System;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Devices;

public abstract class DeviceBase : IDevice
{
    private ConnectionState _state;

    protected DeviceBase(DeviceKind kind, string model, string address)
    {
        Kind = kind;
        Model = model;
        Address = address;
    }

    public DeviceKind Kind { get; }
    public string Model { get; }
    public string Address { get; }

    public ConnectionState State
    {
        get => _state;
        protected set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    public virtual async Task<bool> OpenAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        try
        {
            var ok = await OnOpenAsync(ct).ConfigureAwait(false);
            State = ok ? ConnectionState.Connected : ConnectionState.Faulted;
            AppLog.Info(Kind.ToString(), $"{Model}@{Address} open => {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            State = ConnectionState.Faulted;
            AppLog.Error(Kind.ToString(), $"{Model}@{Address} open failed: {ex.Message}");
            return false;
        }
    }

    public virtual async Task CloseAsync(CancellationToken ct = default)
    {
        try { await OnCloseAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn(Kind.ToString(), $"{Model} close error: {ex.Message}"); }
        State = ConnectionState.Disconnected;
    }

    public virtual Task<bool> SelfTestAsync(CancellationToken ct = default) =>
        Task.FromResult(State == ConnectionState.Connected);

    protected virtual Task<bool> OnOpenAsync(CancellationToken ct) => Task.FromResult(true);
    protected virtual Task OnCloseAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual void Dispose() => CloseAsync().GetAwaiter().GetResult();
}
