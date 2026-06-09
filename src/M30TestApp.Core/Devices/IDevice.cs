using System;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Devices;

public enum ConnectionState { Disconnected, Connecting, Connected, Faulted }

public interface IDevice : IDisposable
{
    DeviceKind Kind { get; }
    string Model { get; }
    string Address { get; }
    ConnectionState State { get; }
    event EventHandler? StateChanged;

    Task<bool> OpenAsync(CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    Task<bool> SelfTestAsync(CancellationToken ct = default);
}

public interface IPressureController : IDevice
{
    Task SetMeasureAsync(CancellationToken ct = default);
    Task SetPressureAsync(float target, string unit, float precision, CancellationToken ct = default);
    Task<float> ReadPressureAsync(CancellationToken ct = default);
    Task<float> ReadUpperLimitAsync(CancellationToken ct = default);
    Task VentAsync(CancellationToken ct = default);
    Task<string> ReadStatusAsync(CancellationToken ct = default);
    /// <summary>
    /// 切换压力控制器的测量类型（绝压/表压/差压）。
    /// 对应 Command.ini 中的 SetAbs / SetGaug / SetDiff 命令。
    /// </summary>
    Task SetPressureTypeAsync(Config.PressureType pressureType, CancellationToken ct = default);
}

public interface IOven : IDevice
{
    Task<bool> SetTempAsync(float celsius, CancellationToken ct = default);
    Task<float> ReadTempAsync(CancellationToken ct = default);
    Task<bool> ReachedAsync(float celsius, float tolerance = 0.5f, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public interface IDmm : IDevice
{
    Task OpenRelayAsync(string channel, CancellationToken ct = default);
    Task CloseRelayAsync(string channel, CancellationToken ct = default);
    /// <summary>查询继电器状态，返回 true = 闭合（阀开），false = 断开（阀关）。</summary>
    Task<bool> QueryRelayStateAsync(string channel, CancellationToken ct = default);
    Task ConfigureVoltageChannelAsync(string channel, CancellationToken ct = default);
    Task<double> ReadConfiguredValueAsync(string channel, CancellationToken ct = default);
    Task<double> ReadVoltageAsync(string channel, CancellationToken ct = default);
    Task<double> ReadResistanceAsync(string channel, CancellationToken ct = default);
}

public interface IDac : IDevice
{
    Task<float> ReadUsourceAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default);
    Task<float> ReadIsourceAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default);
    Task<float> ReadUsigAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default);
    Task<float> ReadUtAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default);
}

public interface IPowerSupply : IDevice
{
    Task SetVoltageAsync(float volts, CancellationToken ct = default);
    Task SetCurrentAsync(float amps, CancellationToken ct = default);
    Task OutputOnAsync(CancellationToken ct = default);
    Task OutputOffAsync(CancellationToken ct = default);
}

public interface IBoard : IDevice
{
    Task OpenChannelAsync(string channel, CancellationToken ct = default);
    Task CloseChannelAsync(string channel, CancellationToken ct = default);
}
