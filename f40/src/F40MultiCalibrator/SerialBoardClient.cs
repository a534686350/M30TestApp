using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace F40MultiCalibrator;

public sealed class SerialBoardClient : IDisposable
{
	private readonly SerialPort _port;

	private readonly byte _addr;

	private readonly int _timeout;

	private readonly Action<string, bool> _log;

	public bool IsOpen => _port.IsOpen;

	public SerialBoardClient(string com, int baud, int dataBits, Parity parity, StopBits stopBits, byte addr, int timeout, Action<string, bool> log)
	{
		_addr = addr;
		_timeout = timeout;
		_log = log;
		_port = new SerialPort(com, baud, parity, dataBits, stopBits)
		{
			ReadTimeout = timeout,
			WriteTimeout = timeout,
			DtrEnable = false,
			RtsEnable = false
		};
	}

	public void Open()
	{
		_port.Open();
	}

	public Task<byte[]> RequestAsync(byte function, byte[] payload, int expectedMinLen, CancellationToken ct)
	{
		return SendAsync(Build(_addr, function, payload), expectedMinLen, ct);
	}

	public Task<byte[]> RequestAsync(byte addr, byte function, byte[] payload, int expectedMinLen, CancellationToken ct)
	{
		return SendAsync(Build(addr, function, payload), expectedMinLen, ct);
	}

	public Task<byte[]> WriteConfigAsync(byte slot, string group, IReadOnlyList<byte> config4, CancellationToken ct)
	{
		return WriteConfigAsync(_addr, slot, group, config4, ct);
	}

	public Task<byte[]> WriteConfigAsync(byte addr, byte slot, string group, IReadOnlyList<byte> config4, CancellationToken ct)
	{
		if (config4.Count != 4)
		{
			throw new ArgumentException("配置必须是4字节，例如 CC050300", "config4");
		}
		int num;
		if (!(group == "1415"))
		{
			if (!(group == "0304"))
			{
				throw new ArgumentException("寄存器组合必须是0304或1415", "group");
			}
			num = 96;
		}
		else
		{
			num = 86;
		}
		byte fn = (byte)num;
		return SendConfigExpectAsync(Build(addr, fn, new byte[1] { slot }.Concat(config4).ToArray()), addr, fn, slot, ct);
	}

	public async Task WriteCoefficientsAsync(byte slot, IReadOnlyList<int> coeff, CancellationToken ct)
	{
		await WriteCoefficientsAsync(_addr, slot, coeff, ct);
	}

	public async Task WriteCoefficientsAsync(byte addr, byte slot, IReadOnlyList<int> coeff, CancellationToken ct)
	{
		const byte writeModeSlot = 1;
		_log($"F40原程序兼容写系数：0x63/0x61固定Slot{writeModeSlot}，0x11写目标Slot{slot}", arg2: true);
		await SendExpectAsync(Build(addr, 99, writeModeSlot), addr, 5, 99, writeModeSlot, ct);
		try
		{
			byte[] response = await SendExpectAsync(Build(addr, 17, BuildWritePayload(slot, coeff)), addr, 5, 17, slot, ct);
			if (response.Length >= 45)
			{
				TryLogCoefficientFrame("0x11写系数回包", response, 17, slot, coeff);
			}
			else
			{
				_log($"0x11写系数ACK有效：Slot{slot} RX={Hex(response)}", arg2: true);
			}
		}
		finally
		{
			await SendExpectAsync(Build(addr, 97, writeModeSlot), addr, 5, 97, writeModeSlot, CancellationToken.None);
		}
	}

	public async Task EnsureNormalOutputModeAsync(byte addr, CancellationToken ct)
	{
		const byte writeModeSlot = 1;
		_log($"板卡{addr}标定前检查：发送0x61退出OWI并确认正常输出模式", arg2: true);
		await SendExpectAsync(Build(addr, 97, writeModeSlot), addr, 5, 97, writeModeSlot, ct);
		_log($"板卡{addr}已响应，正常输出模式就绪", arg2: true);
	}

	private bool TryLogCoefficientFrame(string label, byte[] rsp, byte fn, byte slot, IReadOnlyList<int> expected)
	{
		if (rsp.Length < 45 || rsp[1] != fn || rsp[2] != slot || !CrcOk(rsp))
		{
			_log($"{label}：Slot{slot} 未返回完整10系数帧，RX={Hex(rsp)}", arg2: true);
			return false;
		}
		int[] array = new int[10];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = BitConverter.ToInt32(rsp, 3 + i * 4);
		}
		bool flag = expected.Take(10).SequenceEqual(array);
		_log($"{label} Slot{slot} 前10项{(flag ? "OK" : "不一致")}：{string.Join(",", array)}", arg2: true);
		if (!flag)
		{
			_log($"{label} Slot{slot} 期望：{string.Join(",", expected.Take(10))}", arg2: true);
		}
		return flag;
	}

	private async Task<byte[]> SendExpectAsync(byte[] req, int minLen, byte fn, byte slot, CancellationToken ct)
	{
		return await SendExpectAsync(req, _addr, minLen, fn, slot, ct);
	}

	private async Task<byte[]> SendExpectAsync(byte[] req, byte addr, int minLen, byte fn, byte slot, CancellationToken ct)
	{
		byte[] rsp = await SendAsync(req, minLen, ct);
		if (rsp.Length < 3 || rsp[0] != addr || rsp[1] != fn || rsp[2] != slot || !CrcOk(rsp))
		{
			throw new InvalidDataException("板卡响应异常：" + Hex(rsp));
		}
		return rsp;
	}

	private async Task<byte[]> SendConfigExpectAsync(byte[] req, byte addr, byte fn, byte slot, CancellationToken ct)
	{
		byte[] rsp = await SendAsync(req, 4, ct);
		if (!CrcOk(rsp) || rsp.Length < 4 || rsp[0] != addr || rsp[1] != fn)
		{
			throw new InvalidDataException("板卡写配置响应异常：" + Hex(rsp));
		}
		if (rsp.Length >= 5 && rsp[2] != slot)
		{
			_log($"写配置ACK未回显当前Slot：期望Slot{slot}，RX={Hex(rsp)}；继续按已收到有效ACK处理", arg2: true);
		}
		return rsp;
	}

	private async Task<byte[]> SendAsync(byte[] req, int minLen, CancellationToken ct)
	{
		_port.ReadTimeout = _timeout;
		_port.WriteTimeout = _timeout;
		_port.DiscardInBuffer();
		_port.DiscardOutBuffer();
		_port.Write(req, 0, req.Length);
		_log("TX " + Hex(req), arg2: false);
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(_timeout);
		List<byte> buf = new List<byte>();
		while (DateTime.UtcNow < deadline)
		{
			ct.ThrowIfCancellationRequested();
			await Task.Delay(20, ct);
			while (_port.BytesToRead > 0)
			{
				byte[] b = new byte[_port.BytesToRead];
				int n = _port.Read(b, 0, b.Length);
				buf.AddRange(b.Take(n));
			}
			if (buf.Count >= minLen && CrcOk(buf.ToArray()))
			{
				break;
			}
		}
		if (buf.Count == 0)
		{
			throw new TimeoutException("板卡无响应");
		}
		byte[] rsp = buf.ToArray();
		_log("RX " + Hex(rsp), arg2: false);
		return rsp;
	}

	private static byte[] BuildWritePayload(byte slot, IReadOnlyList<int> coeff)
	{
		if (coeff.Count != 10)
		{
			throw new ArgumentException("系数必须10个");
		}
		List<byte> list = new List<byte> { slot };
		foreach (int item in coeff)
		{
			list.AddRange(BitConverter.GetBytes(item));
		}
		return list.ToArray();
	}

	private static byte[] Build(byte addr, byte fn, params byte[] payload)
	{
		List<byte> list = new List<byte> { addr, fn };
		list.AddRange(payload);
		ushort num = Crc16(list.ToArray());
		list.Add((byte)(num & 0xFF));
		list.Add((byte)(num >> 8));
		return list.ToArray();
	}

	private static ushort Crc16(ReadOnlySpan<byte> data)
	{
		ushort num = ushort.MaxValue;
		ReadOnlySpan<byte> readOnlySpan = data;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			num ^= b;
			for (int j = 0; j < 8; j++)
			{
				num = (ushort)(((num & 1) != 0) ? ((num >> 1) ^ 0xA001) : (num >> 1));
			}
		}
		return num;
	}

	private static bool CrcOk(byte[] frame)
	{
		if (frame.Length < 4)
		{
			return false;
		}
		ushort num = Crc16(frame.AsSpan(0, frame.Length - 2));
		ushort num2 = (ushort)(frame[^2] | (frame[^1] << 8));
		return num == num2;
	}

	private static string Hex(IEnumerable<byte> data)
	{
		return string.Join(" ", data.Select((byte b) => b.ToString("X2")));
	}

	public void Dispose()
	{
		_port.Dispose();
	}
}
