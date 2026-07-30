using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace F40MultiCalibrator;

internal static class CalibrationL6
{
	private static readonly string DllPath = Path.Combine(AppContext.BaseDirectory, "support", "CalibrationL6.dll");

	static CalibrationL6()
	{
		NativeLibrary.SetDllImportResolver(typeof(CalibrationL6).Assembly, (string libraryName, Assembly assembly, DllImportSearchPath? searchPath) => string.Equals(libraryName, "CalibrationL6.dll", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(DllPath) : IntPtr.Zero);
	}

	public static void EnsureAvailable()
	{
		if (!File.Exists(DllPath))
		{
			throw new FileNotFoundException("缺少标定计算库，已阻止测压和写板卡。请保留完整的 support 文件夹：" + DllPath, DllPath);
		}
		try
		{
			IntPtr handle = NativeLibrary.Load(DllPath);
			NativeLibrary.Free(handle);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"标定计算库无法加载，已阻止测压和写板卡。当前进程={(Environment.Is64BitProcess ? "x64" : "x86")}，文件={DllPath}：{ex.Message}", ex);
		}
	}

	[DllImport("CalibrationL6.dll", CallingConvention = CallingConvention.Cdecl)]
	public static extern int CalculateCoefficients([Out] int[] coefficients, [Out] int[] negCoeffs, int numPoints, int selcoeffs, int caltype, [In] double[] bridgeRaw, [In] double[] bridgeDesired, [In] double[] tempRaw, [In] double[] tempDesired);

	[DllImport("CalibrationL6.dll", CallingConvention = CallingConvention.Cdecl)]
	public static extern int VerifyCoefficients([In] int[] coefficients);

	[DllImport("CalibrationL6.dll", CallingConvention = CallingConvention.StdCall)]
	public static extern double GetCorrectedBridge([In] int[] coefficients, double rawBridge, double rawTemp);
}
