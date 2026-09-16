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

    public Task ConfigureVoltageChannelAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "SetVol", "@" + channel);
        return Task.CompletedTask;
    }

    public async Task<double> ReadConfiguredValueAsync(string channel, CancellationToken ct = default)
    {
        SimTrace.Tx(_cmds, Model, "ReadValue");
        await Task.Delay(1, ct);
        var v = 0.005 + _rng.NextDouble() * 0.0001;
        SimTrace.Rx(Model, SimTrace.F(v));
        return v;
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

    // ── 现场样例分布（20260910 142426-08 HPT-LP-K11.1-K10-D05(性能测试).xlsx） ──
    // 桥阻 R ≈ 6160 Ω @25℃，随温度 +5.2 Ω/℃；Usig 零点 ≈ 0 mV（±3）、灵敏度 ≈ 6.4~8.2 mV/kPa，
    // 幅值随温度 -0.19%/℃。调试模式按这套量级出数，避免出现报表里一眼假的数值。
    private const double UsourceVolts = 2.5;
    private const double BridgeBaseOhm = 6160.0;
    private const double BridgeTcoOhmPerC = 5.2;
    private const double UsigTempCoeffPerC = 0.00189;

    public SimDac(string model, string address, CommandDictionary cmds)
        : base(DeviceKind.Dac, model, address) { _cmds = cmds; }

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        DeviceBus.Info(Model, $"DAC opened on {Address}");
        return Task.FromResult(true);
    }

    private async Task<float> SimRead(string action, string a1, string a2, double value, CancellationToken ct)
    {
        DeviceBus.Tx(Model, $"[{action}] addr1={a1} addr2={a2}");
        await Task.Delay(3, ct);
        var v = (float)value;
        DeviceBus.Rx(Model, SimTrace.F(v));
        return v;
    }

    /// <summary>工位指纹（0~1）：同一个工位每次仿真都得到同样的个体差异，不随运行漂移。</summary>
    private static double SlotHash(string a1, string a2)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in a1 ?? "") h = h * 31 + ch;
            h = h * 31 + '|';
            foreach (var ch in a2 ?? "") h = h * 31 + ch;
            return (h & 0x7FFFFFFF) % 10000 / 10000.0;
        }
    }

    /// <summary>桥阻（Ω）：工位个体差异 ±55 Ω，温度系数 +5.2 Ω/℃，叠加 ±1 Ω 采集抖动。</summary>
    private double BridgeResistance(string a1, string a2, double tempC)
    {
        var perSlot = (SlotHash(a1, a2) - 0.5) * 110.0;
        var temperature = BridgeTcoOhmPerC * (tempC - 25.0);
        var noise = (_rng.NextDouble() - 0.5) * 2.0;
        return BridgeBaseOhm + perSlot + temperature + noise;
    }

    public Task<float> ReadUsourceAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
        => SimRead("Usource", a1, a2, UsourceVolts + (_rng.NextDouble() - 0.5) * 0.002, ct);

    /// <summary>Isource = Usource / R，保证报表 R(Ω) 列落在现场量级。</summary>
    public Task<float> ReadIsourceAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
        => SimRead("Isource", a1, a2, UsourceVolts / BridgeResistance(a1, a2, t), ct);

    public Task<float> ReadUsigAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
    {
        var h = SlotHash(a1, a2);
        var zero = (h - 0.45) * 6.0;              // 零点：-2.7 ~ +3.3 mV（现场样例实测 -0.4 ~ 3.7）
        var sensitivity = 6.4 + h * 1.8;          // 灵敏度：6.4 ~ 8.2 mV/压力单位
        var temperature = 1.0 - UsigTempCoeffPerC * (t - 25.0);
        var noise = (_rng.NextDouble() - 0.5) * 0.004;
        return SimRead("Usig", a1, a2, (zero + sensitivity * p) * temperature + noise, ct);
    }

    public Task<float> ReadUtAsync(float p, float t, int v, string a1, string a2, CancellationToken ct = default)
    {
        var offset = (SlotHash(a1, a2) - 0.5) * 0.02;   // 工位个体差异 ±10 mV
        var noise = (_rng.NextDouble() - 0.5) * 0.0004;
        return SimRead("UT", a1, a2, 1.0 + t / 100.0 + offset + noise, ct);
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
