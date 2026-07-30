using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace F40MultiCalibrator;

public sealed class OvenTcpClient : IOvenClient, IDisposable
{
	private readonly string _host;
	private readonly int _port;
	private readonly int _timeout;
	private readonly Action<string, bool> _log;
	private readonly object _sync = new object();

	private TcpClient? _client;
	private NetworkStream? _stream;

	public OvenTcpClient(string host, int port, byte unitId, int timeout, Action<string, bool> log)
	{
		_host = host;
		_port = port;
		_timeout = timeout;
		_log = log;
	}

	public void Open()
	{
		DisposeConnection();
		TcpClient client = new TcpClient();
		Task connect = client.ConnectAsync(_host, _port);
		if (!connect.Wait(_timeout))
		{
			client.Dispose();
			throw new TimeoutException($"烘箱TCP连接超时：{_host}:{_port}");
		}
		connect.GetAwaiter().GetResult();
		_client = client;
		_stream = client.GetStream();
		_stream.ReadTimeout = _timeout;
		_stream.WriteTimeout = _timeout;
		_log($"OVEN TCP TEXT OPEN {_host}:{_port}", true);
	}

	public void Write(string cmd)
	{
		lock (_sync)
		{
			WriteCore(cmd);
		}
	}

	public string Query(string cmd)
	{
		lock (_sync)
		{
			WriteCore(cmd);
			NetworkStream stream = _stream ?? throw new InvalidOperationException("烘箱TCP未打开");
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(_timeout);
			StringBuilder response = new StringBuilder();
			byte[] buffer = new byte[256];
			while (DateTime.UtcNow < deadline)
			{
				if (stream.DataAvailable)
				{
					int count = stream.Read(buffer, 0, buffer.Length);
					if (count <= 0)
					{
						break;
					}
					response.Append(Encoding.ASCII.GetString(buffer, 0, count));
					string current = response.ToString();
					if (current.Contains('\r') || current.Contains('\n'))
					{
						break;
					}
				}
				else
				{
					Thread.Sleep(50);
				}
			}
			string text = response.ToString().Trim();
			if (text.Length == 0)
			{
				throw new TimeoutException("烘箱TCP无响应");
			}
			_log("OVEN TCP RX " + text.Replace("\r", "\\r").Replace("\n", "\\n"), false);
			return text;
		}
	}

	public double QueryNumber(string cmd)
	{
		string text = Query(cmd);
		Match match = Regex.Match(text, "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[Ee][-+]?\\d+)?");
		if (!match.Success)
		{
			throw new FormatException("烘箱返回值不能解析为数字：" + text);
		}
		return double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
	}

	private void WriteCore(string cmd)
	{
		NetworkStream stream = _stream ?? throw new InvalidOperationException("烘箱TCP未打开");
		string payload = cmd.Contains('\r') || cmd.Contains('\n') ? cmd : cmd + "\r\n";
		byte[] bytes = Encoding.ASCII.GetBytes(payload);
		_log("OVEN TCP TX " + payload.Replace("\r", "\\r").Replace("\n", "\\n"), false);
		stream.Write(bytes, 0, bytes.Length);
		stream.Flush();
	}

	public void Dispose()
	{
		DisposeConnection();
	}

	private void DisposeConnection()
	{
		try
		{
			_stream?.Dispose();
		}
		catch
		{
		}
		try
		{
			_client?.Dispose();
		}
		catch
		{
		}
		_stream = null;
		_client = null;
	}
}
