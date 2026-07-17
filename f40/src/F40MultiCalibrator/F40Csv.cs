using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace F40MultiCalibrator;

internal static class F40Csv
{
	public static List<F40SlotRow> Load(string path)
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		byte[] bytes = ReadAllBytesShared(path);
		using StringReader reader = new StringReader(DetectEncoding(bytes).GetString(bytes));
		List<string> list = new List<string>();
		string? line;
		while ((line = reader.ReadLine()) != null)
		{
			if (!string.IsNullOrWhiteSpace(line))
			{
				list.Add(line);
			}
		}
		if (list.Count < 2)
		{
			throw new InvalidDataException("CSV没有数据行");
		}
		List<F40SlotRow> list2 = new List<F40SlotRow>();
		for (int rowIndex = 1; rowIndex < list.Count; rowIndex++)
		{
			string item = list[rowIndex];
			string[] array = SplitCsv(item);
			if (array.Length >= 46)
			{
				int slot;
				try
				{
					slot = ParseSlot(array[0]);
				}
				catch (Exception ex)
				{
					throw new InvalidDataException($"CSV第{rowIndex + 1}行 Slot不能解析：{array[0]}", ex);
				}
				int testResult = ParseInteger(array[2], allowOutOfRangeSentinel: false, $"Slot{slot} TestResult");
				double[] array2 = new double[7];
				double[] array3 = new double[7];
				double[] array4 = new double[7];
				double[] array5 = new double[7];
				int num = 8;
				for (int num2 = 0; num2 < 7; num2++)
				{
					array2[num2] = D(array[num++]);
					array3[num2] = D(array[num++]);
					array4[num2] = D(array[num++]);
					array5[num2] = D(array[num++]);
				}
				int[] array6 = new int[10];
				for (int num3 = 0; num3 < 10; num3++)
				{
					array6[num3] = ParseInteger(array[36 + num3], testResult != 1, $"Slot{slot} 系数{num3 + 1}");
				}
				list2.Add(new F40SlotRow
				{
					Slot = slot,
					Serial = array[1],
					TestResult = testResult,
					BridgeRaw = array2,
					BridgeDesiredPercent = array3,
					OriginalBridgeDesiredPercent = array3.ToArray(),
					TempRaw = array4,
					TempDesiredDeg = array5,
					OriginalCoefficients = array6,
					Coefficients = array6.ToArray(),
					NewMinPercent = array3.Where((double x) => Math.Abs(x - 10.0) < 1.0).DefaultIfEmpty(10.0).Average(),
					NewMidPercent = array3.Where((double x) => Math.Abs(x - 50.0) < 1.0).DefaultIfEmpty(50.0).Average(),
					NewMaxPercent = array3.Where((double x) => Math.Abs(x - 90.0) < 1.0).DefaultIfEmpty(90.0).Average()
				});
			}
		}
		return list2.OrderBy((F40SlotRow x) => x.Slot).ToList();
	}

	private static byte[] ReadAllBytesShared(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using MemoryStream copy = new MemoryStream();
		stream.CopyTo(copy);
		return copy.ToArray();
	}

	private static Encoding DetectEncoding(byte[] bytes)
	{
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

	private static int ParseSlot(string s)
	{
		return int.Parse(Regex.Match(s, "\\d+").Value, CultureInfo.InvariantCulture);
	}

	private static double D(string s)
	{
		return double.Parse(s.Replace("℃", "").Trim(), CultureInfo.InvariantCulture);
	}

	private static int ParseInteger(string s, bool allowOutOfRangeSentinel, string context)
	{
		s = s.Trim();
		if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return result;
		}
		if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2))
		{
			if (result2 > 2147483647.0 || result2 < -2147483648.0)
			{
				if (allowOutOfRangeSentinel)
				{
					return result2 < 0.0 ? int.MinValue : int.MaxValue;
				}
				throw new OverflowException($"{context}整数超范围：{s}");
			}
			return checked((int)Math.Round(result2));
		}
		throw new FormatException($"{context}不能解析整数：{s}");
	}

	private static string[] SplitCsv(string line)
	{
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			switch (c)
			{
			case '"':
				if (flag && i + 1 < line.Length && line[i + 1] == '"')
				{
					stringBuilder.Append('"');
					i++;
				}
				else
				{
					flag = !flag;
				}
				continue;
			case ',':
				if (!flag)
				{
					list.Add(stringBuilder.ToString());
					stringBuilder.Clear();
					continue;
				}
				break;
			}
			stringBuilder.Append(c);
		}
		list.Add(stringBuilder.ToString());
		return list.ToArray();
	}
}
