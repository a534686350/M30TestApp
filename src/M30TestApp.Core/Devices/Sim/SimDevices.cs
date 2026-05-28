using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Devices.Sim;

/// <summary>
/// Helper used by every SIM device to render a command from the loaded
/// <see cref="CommandDictionary"/>. If the model isn't present in Command.ini, a
/// readable fallback string is synthesized so the trace is still useful.
/// </summary>
internal static class SimTrace
{
    public static string Render(CommandDictionary cmds, string model, string action, params object[] args)
    {
        var rendered = cmds.Render(model, action, args);
        if (string.IsNullOrEmpty(rendered))
        {
            var argText = args.Length == 0 ? "" : " " + string.Join(",", args);
            rendered = $"[SIM:{action}]{argText}";
        }
        return rendered;
    }

    public static void Tx(CommandDictionary cmds, string model, string action, params object[] args)
        => DeviceBus.Tx(model, Render(cmds, model, action, args));

    public static void Rx(string model, string payload)
        => DeviceBus.Rx(model, payload);

    public static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

public sealed class SimPressureController : DeviceBase, IPressureController
{
    private readonly CommandDictionary _cmds;
    private float _target;
    private float _current;
    private bool _vented = true;

    public SimPressureController(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Pressure, model, address) { _cmds = cmds; }

    protected override async Task<bool> OnOpenAsync(CancellationToken ct)
    {
        SimTrace.Tx(_cmds, Model, "Open");
        await Task.Delay(20, ct);
        SimTrace.Rx(Model, "OK");
        return true;
    }

    public override async Task<bool> SelfTestAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "SelfTest");
        await Task.Delay(20, ct);
        SimTrace.Rx(Model, "0");
        return true;
    }

    public async Task SetMeasureAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "SetMeasure");
        await Task.Delay(10, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task SetPressureAsync(float target, string unit, float precision, CancellationToken ct = default)
    {
        _target = target; _vented = false;
        SimTrace.Tx(_cmds, Model, "SetPressure", target);
        await Task.Delay(50, ct).ConfigureAwait(false);
        _current = target;
        SimTrace.Rx(Model, $"target={SimTrace.F(target)} {unit}");
    }

    public async Task<float> ReadPressureAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "ReadPressure");
        await Task.Delay(5, ct);
        var v = _vented ? 0f : _current;
        SimTrace.Rx(Model, SimTrace.F(v));
        return v;
    }

    public async Task<float> ReadUpperLimitAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "UpperLimit");
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, "500");
        return 500f;
    }

    public async Task VentAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Vent");
        _vented = true; _current = 0f;
        await Task.Delay(10, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task<string> ReadStatusAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "ReadStatus");
        await Task.Delay(5, ct);
        var s = _vented ? "VENT" : "STABLE";
        SimTrace.Rx(Model, s);
        return s;
    }

    public async Task SetPressureTypeAsync(Config.PressureType pressureType, CancellationToken ct = default)
    {
        var action = pressureType switch
        {
            Config.PressureType.Absolute     => "SetAbs",
            Config.PressureType.Differential => "SetDiff",
            _                                => "SetGaug",
        };
        SimTrace.Tx(_cmds, Model, action);
        await Task.Delay(10, ct);
        SimTrace.Rx(Model, "OK");
    }
}

public sealed class SimOven : DeviceBase, IOven
{
    private readonly CommandDictionary _cmds;
    private float _target;
    private float _current = 25f;

    public SimOven(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Oven, model, address) { _cmds = cmds; }

    protected override async Task<bool> OnOpenAsync(CancellationToken ct)
    {
        SimTrace.Tx(_cmds, Model, "Open");
        await Task.Delay(20, ct);
        SimTrace.Rx(Model, "OK");
        return true;
    }

    public async Task<bool> SetTempAsync(float celsius, CancellationToken ct = default)
    {
        _target = celsius;
        SimTrace.Tx(_cmds, Model, "Set", celsius);
        await Task.Delay(20, ct).ConfigureAwait(false);
        SimTrace.Rx(Model, "OK");
        return true;
    }

    public async Task<float> ReadTempAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Read");
        _current += (_target - _current) * 0.3f;
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, SimTrace.F(_current));
        return _current;
    }

    public async Task<bool> ReachedAsync(float celsius, float tolerance = 0.5f, CancellationToken ct = default)
    {
        var v = await ReadTempAsync(ct).ConfigureAwait(false);
        return Math.Abs(v - celsius) <= tolerance;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Stop");
        _target = 25f;
        await Task.Delay(10, ct);
        SimTrace.Rx(Model, "OK");
    }
}

public sealed class SimDmm : DeviceBase, IDmm
{
    private readonly CommandDictionary _cmds;
    private readonly Random _rng = new(20260525);

    public SimDmm(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Dmm, model, address) { _cmds = cmds; }

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        DeviceBus.Info(Model, "DMM ready");
        return Task.FromResult(true);
    }

    public async Task OpenRelayAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Open", "@" + channel);
        await Task.Delay(2, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task CloseRelayAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Close", "@" + channel);
        await Task.Delay(2, ct);
        SimTrace.Rx(Model, "OK");
    }

    public Task<bool> QueryRelayStateAsync(string channel, CancellationToken ct = default)
    {
        return Task.FromResult(false); // 模拟：默认断开（阀关）
    }

    public async Task<double> ReadVoltageAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "SetVol", "@" + channel);
        SimTrace.Tx(_cmds, Model, "ReadValue");
        await Task.Delay(3, ct);
        var v = 0.005 + _rng.NextDouble() * 0.0001;
        SimTrace.Rx(Model, SimTrace.F(v));
        return v;
    }

    public async Task<double> ReadResistanceAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "SetRes", "@" + channel);
        SimTrace.Tx(_cmds, Model, "ReadValue");
        await Task.Delay(3, ct);
        var v = 12000.0 + _rng.NextDouble() * 5;
        SimTrace.Rx(Model, SimTrace.F(v));
        return v;
    }
}

public sealed class SimDac : DeviceBase, IDac
{
    private readonly CommandDictionary _cmds;
    private readonly Random _rng = new(42);

    public SimDac(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Dac, model, address) { _cmds = cmds; }

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        DeviceBus.Info(Model, $"DAC opened on {Address}");
        return Task.FromResult(true);
    }

    private async Task<float> SimRead(string action, string a1, string a2, float baseline, float swing, CancellationToken ct)
    {
        DeviceBus.Tx(Model, $"[{action}] addr1={a1} addr2={a2}");
        await Task.Delay(3, ct);
        var v = baseline + (float)(_rng.NextDouble() * swing);
        DeviceBus.Rx(Model, SimTrace.F(v));
        return v;
    }

    public Task<float> ReadUsourceAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
        => SimRead("Usource", a1, a2, 2.5f, 0.01f, ct);

    public Task<float> ReadIsourceAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
        => SimRead("Isource", a1, a2, 0.001f, 1e-6f, ct);

    public async Task<float> ReadUsigAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
    {
        DeviceBus.Tx(Model, $"[Usig] p={SimTrace.F(p)} T={SimTrace.F(t)} addr1={a1} addr2={a2}");
        await Task.Delay(3, ct);
        var val = (float)(0.5 + (p / 100.0) * 4.5 + _rng.NextDouble() * 0.001);
        DeviceBus.Rx(Model, SimTrace.F(val));
        return val;
    }

    public async Task<float> ReadUtAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
    {
        DeviceBus.Tx(Model, $"[UT] T={SimTrace.F(t)} addr1={a1} addr2={a2}");
        await Task.Delay(3, ct);
        var val = (float)(1.0 + (t / 100.0) + _rng.NextDouble() * 0.001);
        DeviceBus.Rx(Model, SimTrace.F(val));
        return val;
    }
}

public sealed class SimPower : DeviceBase, IPowerSupply
{
    private readonly CommandDictionary _cmds;

    public SimPower(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Power, model, address) { _cmds = cmds; }

    public async Task SetVoltageAsync(float volts, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "VoltageSource", volts);
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task SetCurrentAsync(float amps, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "CurrentSource", amps);
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task OutputOnAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "OutputON");
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task OutputOffAsync(CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "OutputOFF");
        await Task.Delay(5, ct);
        SimTrace.Rx(Model, "OK");
    }
}

public sealed class SimBoard : DeviceBase, IBoard
{
    private readonly CommandDictionary _cmds;

    public SimBoard(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Board, model, address) { _cmds = cmds; }

    public async Task OpenChannelAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Open");
        DeviceBus.Info(Model, $"channel {channel}");
        await Task.Delay(2, ct);
        SimTrace.Rx(Model, "OK");
    }

    public async Task CloseChannelAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "Close");
        DeviceBus.Info(Model, $"channel {channel}");
        await Task.Delay(2, ct);
        SimTrace.Rx(Model, "OK");
    }
}
