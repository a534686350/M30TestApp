using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace F40MultiCalibrator;

internal sealed class IniFile
{
	private readonly Dictionary<string, Dictionary<string, string>> _data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

	public IEnumerable<string> Sections => _data.Keys;

	public IReadOnlyDictionary<string, string> Section(string section)
	{
		Dictionary<string, string> value;
		return _data.TryGetValue(section, out value) ? value : new Dictionary<string, string>();
	}

	public static IniFile Load(string path)
	{
		IniFile iniFile = new IniFile();
		if (!File.Exists(path))
		{
			return iniFile;
		}
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		string key = "";
		string[] array = File.ReadAllLines(path, DetectEncoding(path));
		foreach (string text in array)
		{
			string text2 = text.Trim();
			if (text2.Length == 0 || text2.StartsWith(";") || text2.StartsWith("#"))
			{
				continue;
			}
			Match match = Regex.Match(text2, "^\\[(.+)\\]$");
			if (match.Success)
			{
				key = match.Groups[1].Value.Trim();
				if (!iniFile._data.ContainsKey(key))
				{
					iniFile._data[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				}
				continue;
			}
			int num = text2.IndexOf('=');
			if (num >= 0)
			{
				string key2 = text2.Substring(0, num).Trim();
				string text3 = text2;
				int num2 = num + 1;
				string v = text3.Substring(num2, text3.Length - num2).Trim();
				v = Unquote(v);
				if (!iniFile._data.ContainsKey(key))
				{
					iniFile._data[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				}
				iniFile._data[key][key2] = v;
			}
		}
		return iniFile;
	}

	public string Get(string section, string key, string fallback)
	{
		Dictionary<string, string> value;
		string value2;
		return (_data.TryGetValue(section, out value) && value.TryGetValue(key, out value2)) ? value2 : fallback;
	}

	public int GetInt(string section, string key, int fallback)
	{
		int result;
		return int.TryParse(Get(section, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	public decimal GetDecimal(string section, string key, decimal fallback)
	{
		decimal result;
		return decimal.TryParse(Get(section, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	public bool GetBool(string section, string key, bool fallback)
	{
		string text = Get(section, key, fallback ? "TRUE" : "FALSE");
		return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("YES", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("ON", StringComparison.OrdinalIgnoreCase);
	}

	private static string Unquote(string v)
	{
		if (v.Length >= 2 && v[0] == '"')
		{
			string text = v;
			if (text[text.Length - 1] == '"')
			{
				string text2 = v;
				v = text2.Substring(1, text2.Length - 1 - 1);
			}
		}
		return v.Replace("\\\"", "\"");
	}

	private static Encoding DetectEncoding(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		try
		{
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		}
		catch
		{
			return Encoding.GetEncoding("GB18030");
		}
	}
}
