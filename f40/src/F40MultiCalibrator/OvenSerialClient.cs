using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace F40MultiCalibrator;

public sealed class OvenSerialClient : IOvenClient, IDisposable
{
	private readonly SerialPort _port;

	private readonly int _timeout;

	private readonly Action<string, bool> _log;

	public OvenSerialClient(string com, int baud, int dataBits, Parity parity, StopBits stopBits, int timeout, Action<string, bool> log)
	{
		_timeout = timeout;
		_log = log;
		_port = new SerialPort(com, baud, parity, dataBits, stopBits)
		{
			ReadTimeout = timeout,
			WriteTimeout = timeout,
			NewLine = "\r\n",
			DtrEnable = false,
			RtsEnable = false
		};
	}

	public void Open()
	{
		_port.Open();
		_log($"OVEN OPEN {_port.PortName} {_port.BaudRate},{_port.DataBits},{_port.Parity},{_port.StopBits}", arg2: true);
	}

	public void Write(string cmd)
	{
		_port.DiscardInBuffer();
		_port.DiscardOutBuffer();
		_log("OVEN TX " + cmd.Replace("\r", "\\r").Replace("\n", "\\n"), arg2: false);
		if (cmd.Contains('\r') || cmd.Contains('\n'))
		{
			_port.Write(cmd);
		}
		else
		{
			_port.WriteLine(cmd);
		}
	}

	public string Query(string cmd)
	{
		Write(cmd);
		DateTime dateTime = DateTime.UtcNow.AddMilliseconds(_timeout);
		StringBuilder stringBuilder = new StringBuilder();
		while (DateTime.UtcNow < dateTime)
		{
			Thread.Sleep(50);
			string value = _port.ReadExisting();
			if (!string.IsNullOrEmpty(value))
			{
				stringBuilder.Append(value);
			}
			if (stringBuilder.Length > 0 && (stringBuilder.ToString().Contains('\n') || stringBuilder.ToString().Contains('\r')))
			{
				break;
			}
		}
		string text = stringBuilder.ToString().Trim();
		if (text.Length == 0)
		{
			throw new TimeoutException("烘箱无响应");
		}
		_log("OVEN RX " + text, arg2: false);
		return text;
	}

	public double QueryNumber(string cmd)
	{
		string text = Query(cmd);
		Match match = Regex.Match(text, "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[Ee][-+]?\\d+)?");
		if (!match.Success)
		{
			throw new FormatException("烘箱返回值不能解析为数字：" + text);
		}
		return double.Parse(match.Value, CultureInfo.InvariantCulture);
	}

	public void Dispose()
	{
		_port.Dispose();
	}
}
