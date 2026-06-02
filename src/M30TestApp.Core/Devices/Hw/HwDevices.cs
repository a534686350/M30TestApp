using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ivi.Visa.Interop;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Devices.Hw;

internal sealed class VisaSession : IDisposable
{
    private readonly string _device;
    private readonly string _address;
    private FormattedIO488? _io;
    private ResourceManager? _rm;

    public VisaSession(string device, string address)
    {
        _device = device;
        _address = address;
    }

    public void Open()
    {
        if (_io is not null) return;
        _rm = new ResourceManager();
        var session = (IMessage)_rm.Open(_address, AccessMode.NO_LOCK, 2000, string.Empty);
        session.Timeout = 10000;
        _io = new FormattedIO488 { IO = session };
        DeviceBus.Info(_device, "VISA open " + _address);
    }

    public void Write(string command)
    {
        Open();
        DeviceBus.Tx(_device, command);
        try
        {
            _io!.WriteString(command, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{_device} @ {_address} 发送失败：{command}；{ex.Message}", ex);
        }
    }

    public string QueryString(string command)
    {
        Write(command);
        try
        {
            var reply = (_io!.ReadString() ?? string.Empty).Trim();
            DeviceBus.Rx(_device, reply);
            return reply;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{_device} @ {_address} 读取失败：{command}；{ex.Message}", ex);
        }
    }

    public double QueryNumber(string command)
    {
        var reply = QueryString(command);
        var match = Regex.Match(reply, @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][-+]?\d+)?");
        if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"{_device} @ {_address} 返回值无法解析为数字：{reply}");
        DeviceBus.Rx(_device, value.ToString("G9", CultureInfo.InvariantCulture));
        return value;
    }

    public void TryClear()
    {
        try { _io?.IO?.Clear(); } catch { }
    }

    public void Dispose()
    {
        try { _io?.IO?.Close(); } catch { }
        _io = null;
        _rm = null;
    }
}

internal static class SerialHelpers
{
    public static Parity ParseParity(string value) => value.Equals("Odd", StringComparison.OrdinalIgnoreCase) || value.Equals("O", StringComparison.OrdinalIgnoreCase)
        ? Parity.Odd
        : value.Equals("Even", StringComparison.OrdinalIgnoreCase) || value.Equals("E", StringComparison.OrdinalIgnoreCase)
            ? Parity.Even
            : Parity.None;

    public static StopBits ParseStopBits(string value) => value switch
    {
        "0" => StopBits.None,
        "1.5" => StopBits.OnePointFive,
        "2" => StopBits.Two,
        _ => StopBits.One
    };

    public static string ToHex(byte[] data)
    {
        if (data.Length == 0) return string.Empty;

        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append(' ');
        }
        return sb.ToString();
    }
}

public sealed class HwOven : DeviceBase, IOven
{
    private readonly DeviceProfile _profile;
    private SerialPort? _port;
    private float _target;

    public HwOven(DeviceProfile profile) : base(DeviceKind.Oven, profile.Model, profile.Address) => _profile = profile;

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        EnsureOpen();
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _port?.Close();
        return Task.CompletedTask;
    }

    public async Task<bool> SetTempAsync(float celsius, CancellationToken ct = default)
    {
        _target = celsius;
        var setReply = await QueryAsync($"TEMP,S{celsius.ToString("0.0", CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (!setReply.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) return false;
        var openReply = await QueryAsync("POWER,ON", ct).ConfigureAwait(false);
        return openReply.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<float> ReadTempAsync(CancellationToken ct = default)
    {
        var reply = await QueryAsync("TEMP?", ct).ConfigureAwait(false);
        // 烘箱返回逗号分隔多值如 "24.8,25.0,155.0,-75.0"，取第一个值作为当前温度
        var first = reply.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? reply;
        return float.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
    }

    public async Task<bool> ReachedAsync(float celsius, float tolerance = 0.5f, CancellationToken ct = default)
    {
        var temp = await ReadTempAsync(ct).ConfigureAwait(false);
        return Math.Abs(temp - celsius) <= tolerance;
    }

    public async Task StopAsync(CancellationToken ct = default) => await QueryAsync("POWER,OFF", ct).ConfigureAwait(false);

    private void EnsureOpen()
    {
        if (_port is { IsOpen: true }) return;
        _port?.Dispose();
        _port = new SerialPort(_profile.Address, _profile.Baud, SerialHelpers.ParseParity(_profile.Parity), _profile.DataBits, SerialHelpers.ParseStopBits(_profile.StopBits))
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true
        };
        DeviceBus.Tx(Model, $"OPEN {_profile.Address} {_profile.Baud} {_profile.Parity} {_profile.DataBits}{_profile.StopBits}");
        _port.Open();
        DeviceBus.Rx(Model, "OK");
    }

    private async Task<string> QueryAsync(string command, CancellationToken ct)
    {
        EnsureOpen();
        _port!.DiscardInBuffer();
        DeviceBus.Tx(Model, command);
        var bytes = Encoding.ASCII.GetBytes(command);
        _port.Write(bytes, 0, bytes.Length);
        await Task.Delay(100, ct).ConfigureAwait(false);
        var reply = _port.BytesToRead > 0 ? _port.ReadExisting().Trim() : "";
        DeviceBus.Rx(Model, reply.Length == 0 ? "NO RX" : reply);
        return reply;
    }
}

public sealed class HwDac : DeviceBase, IDac
{
    private readonly DeviceProfile _profile;
    private SerialPort? _port;

    public HwDac(DeviceProfile profile) : base(DeviceKind.Dac, profile.Model, profile.Address) => _profile = profile;

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        EnsureOpen();
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _port?.Close();
        return Task.CompletedTask;
    }

    public Task<float> ReadUsourceAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default) => ReadValueAsync(2, addr1, addr2, ct);
    public Task<float> ReadIsourceAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default) => ReadValueAsync(3, addr1, addr2, ct);
    public Task<float> ReadUsigAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default) => ReadValueAsync(4, addr1, addr2, ct);
    public async Task<float> ReadUtAsync(float pressure, float tempC, int v, string addr1, string addr2, CancellationToken ct = default)
    {
        // 先发功能码 06 切换 UT 电源到指定 card+channel
        await SwitchUtPowerAsync(addr1, addr2, ct).ConfigureAwait(false);
        // 板卡内部继电器切换后需要稳定时间,否则下一次 05 读取会得到空回复触发重试
        await Task.Delay(UtSettleMs, ct).ConfigureAwait(false);
        // 再发功能码 05 读取 UT 值
        return await ReadValueAsync(5, addr1, addr2, ct).ConfigureAwait(false);
    }

    private static int UtSettleMs => 500;

    /// <summary>
    /// 发送功能码 06 [card, 0x06, channel] + CRC，等待 5 字节回声确认。
    /// </summary>
    private async Task SwitchUtPowerAsync(string cardAddr, string channelAddr, CancellationToken ct)
    {
        EnsureOpen();
        if (!byte.TryParse(cardAddr, out var card) || !byte.TryParse(channelAddr, out var channel))
            throw new InvalidOperationException("采集卡地址或通道地址无效");

        var request = AppendCrc(new[] { card, (byte)6, channel });

        // 重试机制：确保回声正确
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _port!.DiscardInBuffer();
            _port.DiscardOutBuffer();
            DeviceBus.Tx(Model, "Send: " + SerialHelpers.ToHex(request));
            _port.Write(request, 0, request.Length);
            var response = await ReadExactAsync(5, 1500, ct).ConfigureAwait(false);
            DeviceBus.Rx(Model, "Get: " + SerialHelpers.ToHex(response));

            if (response.Length >= 5 && CheckCrc(response))
            {
                // 回声正确，验证是否为发送命令的回声
                if (response[0] == card && response[1] == 6 && response[2] == channel)
                {
                    return; // 回声正确，成功
                }
            }

            DeviceBus.Info(Model, $"UT power switch retry {attempt}: echo invalid len={response.Length}");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        DeviceBus.Info(Model, "UT power switch echo failed after retries, continuing anyway");
    }

    private void EnsureOpen()
    {
        if (_port is { IsOpen: true }) return;
        _port?.Dispose();
        _port = new SerialPort(_profile.Address, _profile.Baud, SerialHelpers.ParseParity(_profile.Parity), _profile.DataBits, SerialHelpers.ParseStopBits(_profile.StopBits))
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true
        };
        DeviceBus.Tx(Model, $"OPEN {_profile.Address} {_profile.Baud} {_profile.Parity} {_profile.DataBits}{_profile.StopBits}");
        _port.Open();
        DeviceBus.Rx(Model, "OK");
    }

    private async Task<float> ReadValueAsync(byte function, string cardAddr, string channelAddr, CancellationToken ct)
    {
        EnsureOpen();
        if (!byte.TryParse(cardAddr, out var card) || !byte.TryParse(channelAddr, out var channel))
            throw new InvalidOperationException("采集卡地址或通道地址无效");

        var request = AppendCrc(new[] { card, function, channel });
        byte[] response = Array.Empty<byte>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _port!.DiscardInBuffer();
            _port.DiscardOutBuffer();
            DeviceBus.Tx(Model, "Send: " + SerialHelpers.ToHex(request));
            _port.Write(request, 0, request.Length);
            response = await ReadExactAsync(9, 1500, ct).ConfigureAwait(false);
            DeviceBus.Rx(Model, "Get: " + SerialHelpers.ToHex(response));
            if (response.Length >= 9 && CheckCrc(response))
                return BitConverter.ToSingle(response.AsSpan(3, 4));

            DeviceBus.Info(Model, $"Read retry {attempt}: invalid response length={response.Length}");
            // 空回复多半是板卡尚未稳定,加长间隔再试
            await Task.Delay(response.Length == 0 ? 300 : 100, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("采集板返回 CRC 校验失败或数据长度不足");
    }

    private async Task<byte[]> ReadExactAsync(int expectedLength, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var buffer = new byte[expectedLength];
        var offset = 0;

        while (sw.ElapsedMilliseconds < timeoutMs && offset < expectedLength)
        {
            ct.ThrowIfCancellationRequested();
            var count = _port!.BytesToRead;
            if (count > 0)
            {
                var read = _port.Read(buffer, offset, Math.Min(count, expectedLength - offset));
                offset += read;
                if (offset >= expectedLength) break;
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        while (_port!.BytesToRead > 0 && offset < expectedLength)
            offset += _port.Read(buffer, offset, Math.Min(_port.BytesToRead, expectedLength - offset));

        if (offset == expectedLength) return buffer;
        Array.Resize(ref buffer, offset);
        return buffer;
    }

    private static byte[] AppendCrc(byte[] data)
    {
        var crc = ModbusCrc(data);
        var frame = new byte[data.Length + 2];
        Buffer.BlockCopy(data, 0, frame, 0, data.Length);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private static bool CheckCrc(byte[] data)
    {
        if (data.Length < 3) return false;
        var crc = ModbusCrc(data.AsSpan(0, data.Length - 2));
        return data[^2] == (byte)(crc & 0xFF) && data[^1] == (byte)(crc >> 8);
    }

    private static ushort ModbusCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }
}

public sealed class HwPressureController : DeviceBase, IPressureController
{
    private readonly CommandDictionary _commands;
    private readonly VisaSession _visa;

    public HwPressureController(DeviceProfile profile, CommandDictionary commands) : base(DeviceKind.Pressure, profile.Model, profile.Address)
    {
        _commands = commands;
        _visa = new VisaSession(profile.Model, profile.Address);
    }

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        _visa.Open();
        var open = _commands.Raw(Model, "Open", "*RST");
        if (!string.IsNullOrWhiteSpace(open)) _visa.Write(open);
        _visa.QueryString(_commands.Raw(Model, "Machine Type", "*IDN?"));
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _visa.Dispose();
        return Task.CompletedTask;
    }

    public Task SetMeasureAsync(CancellationToken ct = default)
    {
        _visa.Write(_commands.Raw(Model, "SetMeasure", "*CLS;OUTP:MODE MEAS"));
        return Task.CompletedTask;
    }

    public Task SetPressureAsync(float target, string unit, float precision, CancellationToken ct = default)
    {
        var cmd = _commands.Render(Model, "SetPressure", target, unit, precision);
        if (string.IsNullOrWhiteSpace(cmd))
            cmd = $"UNIT {unit};:PRES {target.ToString(CultureInfo.InvariantCulture)};TOL {precision.ToString(CultureInfo.InvariantCulture)};:OUTP:MODE CONTROL";
        _visa.Write(cmd);
        return Task.CompletedTask;
    }

    public Task<float> ReadPressureAsync(CancellationToken ct = default)
    {
        var cmd = _commands.Raw(Model, "ReadPressure", "MEAS?");
        return Task.FromResult((float)_visa.QueryNumber(cmd));
    }

    public Task<float> ReadUpperLimitAsync(CancellationToken ct = default)
    {
        var cmd = _commands.Raw(Model, "UpperLimit", "CALC:LIM:UPP?");
        if (string.Equals(cmd, "CALC:LIM:UPP?", StringComparison.Ordinal))
            cmd = _commands.Raw(Model, "UpperLimt", cmd);
        return Task.FromResult((float)_visa.QueryNumber(cmd));
    }

    public Task VentAsync(CancellationToken ct = default)
    {
        _visa.Write(_commands.Raw(Model, "Vent", "OUTP:MODE VENT"));
        return Task.CompletedTask;
    }

    public Task<string> ReadStatusAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_visa.QueryString(_commands.Raw(Model, "ReadStatus", ":STAT:OPER:COND?")));
    }

    public Task SetPressureTypeAsync(Config.PressureType pressureType, CancellationToken ct = default)
    {
        var (action, fallback) = pressureType switch
        {
            Config.PressureType.Absolute     => ("SetAbs",  "*CLS;SENSE:MODE ABS"),
            Config.PressureType.Differential => ("SetDiff", "*CLS;SENSE:MODE DIFF"),
            _                                => ("SetGaug", "*CLS;SENSE:MODE GAUG"),
        };
        var cmd = _commands.Raw(Model, action, fallback);
        if (!string.IsNullOrWhiteSpace(cmd))
            _visa.Write(cmd);
        return Task.CompletedTask;
    }

    public override Task<bool> SelfTestAsync(CancellationToken ct = default) => Task.FromResult(_visa.QueryString(_commands.Raw(Model, "SelfTest", "*TST?")).StartsWith("0", StringComparison.OrdinalIgnoreCase));
}

public sealed class HwDmm : DeviceBase, IDmm
{
    private readonly VisaSession _visa;

    public HwDmm(DeviceProfile profile) : base(DeviceKind.Dmm, profile.Model, profile.Address) => _visa = new VisaSession(profile.Model, profile.Address);

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        _visa.Open();
        _visa.QueryString("*IDN?");
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _visa.Dispose();
        return Task.CompletedTask;
    }

    public Task OpenRelayAsync(string channel, CancellationToken ct = default)
    {
        _visa.Write($"ROUT:OPEN (@{channel})");
        return Task.CompletedTask;
    }

    public Task CloseRelayAsync(string channel, CancellationToken ct = default)
    {
        _visa.Write($"ROUT:CLOSE (@{channel})");
        return Task.CompletedTask;
    }

    public Task<bool> QueryRelayStateAsync(string channel, CancellationToken ct = default)
    {
        var reply = _visa.QueryString($"ROUT:OPEN? (@{channel})").Trim();
        // ROUT:OPEN? 返回 "1" = 通道已断开(阀关), "0" = 通道已闭合(阀开)
        // 但实测部分型号返回值相反，此处以 "1" = 阀开 为准
        return Task.FromResult(reply.StartsWith("1"));
    }

    public Task<double> ReadVoltageAsync(string channel, CancellationToken ct = default)
    {
        try
        {
            _visa.Write($"CONF:VOLT:DC 10,(@{channel})");
            return Task.FromResult(_visa.QueryNumber("READ?"));
        }
        catch (Exception ex)
        {
            AppLog.Error("DMM", $"ReadVoltage({channel}) failed: {ex.Message}");
            return Task.FromResult(double.NaN);
        }
    }

    public Task<double> ReadResistanceAsync(string channel, CancellationToken ct = default)
    {
        try
        {
            _visa.Write($"CONF:RES (@{channel})");
            return Task.FromResult(_visa.QueryNumber("READ?"));
        }
        catch (Exception ex)
        {
            AppLog.Error("DMM", $"ReadResistance({channel}) failed: {ex.Message}");
            return Task.FromResult(double.NaN);
        }
    }

    public override Task<bool> SelfTestAsync(CancellationToken ct = default) => Task.FromResult(State == ConnectionState.Connected);
}

public sealed class HwPowerSupply : DeviceBase, IPowerSupply
{
    private readonly CommandDictionary _commands;
    private readonly VisaSession _visa;

    public HwPowerSupply(DeviceProfile profile, CommandDictionary commands) : base(DeviceKind.Power, profile.Model, profile.Address)
    {
        _commands = commands;
        _visa = new VisaSession(profile.Model, profile.Address);
    }

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        _visa.Open();
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _visa.Dispose();
        return Task.CompletedTask;
    }

    public Task SetVoltageAsync(float volts, CancellationToken ct = default)
    {
        _visa.Write(_commands.Render(Model, "VoltageSource", volts));
        return Task.CompletedTask;
    }

    public Task SetCurrentAsync(float amps, CancellationToken ct = default)
    {
        _visa.Write(_commands.Render(Model, "CurrentSource", amps));
        return Task.CompletedTask;
    }

    public Task OutputOnAsync(CancellationToken ct = default)
    {
        _visa.Write(_commands.Raw(Model, "OutputON", "OPR"));
        return Task.CompletedTask;
    }

    public Task OutputOffAsync(CancellationToken ct = default)
    {
        _visa.Write(_commands.Raw(Model, "OutputOFF", "SBY"));
        return Task.CompletedTask;
    }
}

public sealed class HwBoard : DeviceBase, IBoard
{
    private readonly DeviceProfile _profile;
    private SerialPort? _port;

    public HwBoard(DeviceProfile profile) : base(DeviceKind.Board, profile.Model, profile.Address) => _profile = profile;

    protected override Task<bool> OnOpenAsync(CancellationToken ct)
    {
        EnsureOpen();
        return Task.FromResult(true);
    }

    protected override Task OnCloseAsync(CancellationToken ct)
    {
        _port?.Close();
        return Task.CompletedTask;
    }

    public Task OpenChannelAsync(string channel, CancellationToken ct = default) => SendAsync("B3", channel, ct);
    public Task CloseChannelAsync(string channel, CancellationToken ct = default) => SendAsync("B1", channel, ct);

    private void EnsureOpen()
    {
        if (_port is { IsOpen: true }) return;
        _port?.Dispose();
        _port = new SerialPort(_profile.Address, _profile.Baud, SerialHelpers.ParseParity(_profile.Parity), _profile.DataBits, SerialHelpers.ParseStopBits(_profile.StopBits))
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            DtrEnable = true,
            RtsEnable = true
        };
        DeviceBus.Tx(Model, $"OPEN {_profile.Address}");
        _port.Open();
        DeviceBus.Rx(Model, "OK");
    }

    private async Task SendAsync(string command, string channel, CancellationToken ct)
    {
        EnsureOpen();
        var payload = string.IsNullOrWhiteSpace(channel) ? command : $"{command}{channel}";
        DeviceBus.Tx(Model, payload);
        var bytes = Encoding.ASCII.GetBytes(payload);
        _port!.Write(bytes, 0, bytes.Length);
        await Task.Delay(50, ct).ConfigureAwait(false);
        if (_port.BytesToRead > 0) DeviceBus.Rx(Model, _port.ReadExisting().Trim());
    }
}
