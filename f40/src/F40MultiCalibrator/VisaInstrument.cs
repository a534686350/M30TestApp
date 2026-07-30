using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Ivi.Visa.Interop;

namespace F40MultiCalibrator;

public sealed class VisaInstrument : IDisposable
{
	private readonly string _name;

	private readonly string _addr;

	private readonly Action<string, bool> _log;

	private ResourceManager? _rm;

	private FormattedIO488? _io;

	public VisaInstrument(string name, string addr, Action<string, bool> log)
	{
		_name = name;
		_addr = addr;
		_log = log;
	}

	public void Open()
	{
		_rm = (ResourceManager)Activator.CreateInstance(Marshal.GetTypeFromCLSID(new Guid("DB8CBF1C-D6D3-11D4-AA51-00A024EE30BD")));
		IMessage message = (IMessage)_rm.Open(_addr);
		message.Timeout = 10000;
		FormattedIO488 obj = (FormattedIO488)Activator.CreateInstance(Marshal.GetTypeFromCLSID(new Guid("DB8CBF1D-D6D3-11D4-AA51-00A024EE30BD")));
		obj.IO = message;
		_io = obj;
		_log(_name + " VISA OPEN " + _addr, arg2: true);
	}

	public void Write(string cmd)
	{
		_log(_name + " TX " + cmd, arg2: false);
		_io.WriteString(cmd);
	}

	public string Query(string cmd)
	{
		Write(cmd);
		string text = (_io.ReadString() ?? "").Trim();
		_log(_name + " RX " + text, arg2: false);
		return text;
	}

	public string QuerySilent(string cmd)
	{
		_io.WriteString(cmd);
		return (_io.ReadString() ?? "").Trim();
	}

	public double QueryNumber(string cmd)
	{
		string text = Query(cmd);
		Match match = Regex.Match(text, "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[Ee][-+]?\\d+)?");
		if (!match.Success)
		{
			throw new FormatException(_name + "返回值不能解析为数字：" + text);
		}
		return double.Parse(match.Value, CultureInfo.InvariantCulture);
	}

	public double QueryNumberSilent(string cmd)
	{
		string text = QuerySilent(cmd);
		Match match = Regex.Match(text, "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[Ee][-+]?\\d+)?");
		if (!match.Success)
		{
			throw new FormatException(_name + "返回值不能解析为数字：" + text);
		}
		return double.Parse(match.Value, CultureInfo.InvariantCulture);
	}

	public void Dispose()
	{
		try
		{
			_io?.IO?.Close();
		}
		catch
		{
		}
		_io = null;
		_rm = null;
	}
}
