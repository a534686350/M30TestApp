using System;
using System.ComponentModel;
using System.Linq;

namespace F40MultiCalibrator;

public sealed class F40SlotRow : INotifyPropertyChanged
{
	private bool _selected;

	private string _status = "";

	public bool Selected
	{
		get
		{
			return _selected;
		}
		set
		{
			_selected = value;
			Changed("Selected");
		}
	}

	public int Slot { get; set; }

	public byte SlotByte => checked((byte)Slot);

	public string Serial { get; set; } = "";

	public int TestResult { get; set; }

	public string DmmAddress { get; set; } = "";

	public string Channel { get; set; } = "";

	public double PreZeroV { get; set; } = double.NaN;

	public double PreFullV { get; set; } = double.NaN;

	public double ZeroV { get; set; } = double.NaN;

	public double FullV { get; set; } = double.NaN;

	public double PostMidV { get; set; } = double.NaN;

	public double TargetOutputMinV { get; set; } = 0.5;

	public double TargetOutputMaxV { get; set; } = 4.5;

	public double TargetPercentMin { get; set; } = 10.0;

	public double TargetPercentMax { get; set; } = 90.0;

	public double TargetPercentMid => (TargetPercentMin + TargetPercentMax) / 2.0;

	public double NewMinPercent { get; set; } = 10.0;

	public double NewMidPercent { get; set; } = 50.0;

	public double NewMaxPercent { get; set; } = 90.0;

	public double LinearityPercent { get; set; } = double.NaN;

	public string WriteResult { get; set; } = "";

	public string Status
	{
		get
		{
			return _status;
		}
		set
		{
			_status = value;
			Changed("Status");
		}
	}

	public double[] BridgeRaw { get; init; } = Array.Empty<double>();

	public double[] BridgeDesiredPercent { get; init; } = Array.Empty<double>();

	public double[] OriginalBridgeDesiredPercent { get; init; } = Array.Empty<double>();

	public double[] TempRaw { get; init; } = Array.Empty<double>();

	public double[] TempDesiredDeg { get; init; } = Array.Empty<double>();

	public int[] OriginalCoefficients { get; init; } = Array.Empty<int>();

	public int[] Coefficients { get; set; } = Array.Empty<int>();

	public string CoefficientsText => (Coefficients.Length == 10) ? string.Join(",", Coefficients) : string.Join(",", OriginalCoefficients);

	public event PropertyChangedEventHandler? PropertyChanged;

	public void ApplyOutputCorrection(double zeroMeasured, double fullMeasured)
	{
		ApplyTwoPointOutputCorrection(zeroMeasured, fullMeasured);
	}

	public void ApplyTargetDefinitions(double outputMinV, double outputMaxV, double percentMin, double percentMax, bool resetDesiredPercents)
	{
		TargetOutputMinV = outputMinV;
		TargetOutputMaxV = outputMaxV;
		TargetPercentMin = percentMin;
		TargetPercentMax = percentMax;
		if (resetDesiredPercents)
		{
			double[] source = OriginalBridgeDesiredPercent.Length == BridgeDesiredPercent.Length ? OriginalBridgeDesiredPercent : BridgeDesiredPercent;
			if (OriginalBridgeDesiredPercent.Length == BridgeDesiredPercent.Length)
			{
				Array.Copy(OriginalBridgeDesiredPercent, BridgeDesiredPercent, BridgeDesiredPercent.Length);
			}
			NewMinPercent = AverageNearTarget(source, TargetPercentMin);
			NewMidPercent = AverageNearTarget(source, TargetPercentMid);
			NewMaxPercent = AverageNearTarget(source, TargetPercentMax);
		}
		ApplyBridgeDesiredTargets();
	}

	public void ApplyTwoPointOutputCorrection(double zeroMeasured, double fullMeasured)
	{
		ValidateOutputCorrectionInputs(zeroMeasured, fullMeasured);
		double newMinPercent = NewMinPercent;
		double newMaxPercent = NewMaxPercent;
		double num = OriginalOutputSpanPercent() / (fullMeasured - zeroMeasured);
		NewMinPercent = QuantizeOutputTarget(newMinPercent + (TargetOutputMinV - zeroMeasured) * num);
		NewMaxPercent = QuantizeOutputTarget(newMaxPercent + (TargetOutputMaxV - fullMeasured) * num);
		NewMidPercent = (NewMinPercent + NewMaxPercent) / 2.0;
		ApplyBridgeDesiredTargets();
	}

	public void ApplyFullOutputCorrection(double zeroMeasured, double fullMeasured)
	{
		ValidateOutputCorrectionInputs(zeroMeasured, fullMeasured);
		double newMaxPercent = NewMaxPercent;
		double num = OriginalOutputSpanPercent() / (fullMeasured - zeroMeasured);
		NewMaxPercent = QuantizeOutputTarget(newMaxPercent + (TargetOutputMaxV - fullMeasured) * num);
		NewMidPercent = (NewMinPercent + NewMaxPercent) / 2.0;
		ApplyBridgeDesiredTargets();
	}

	public void ApplyZeroOutputCorrection(double zeroMeasured, double fullMeasured)
	{
		ApplyTwoPointOutputCorrection(zeroMeasured, fullMeasured);
	}

	public double CalculateFullCorrectionDelta(double zeroMeasured, double fullMeasured)
	{
		ValidateOutputCorrectionInputs(zeroMeasured, fullMeasured);
		double corrected = QuantizeOutputTarget(NewMaxPercent + (TargetOutputMaxV - fullMeasured) * OriginalOutputSpanPercent() / (fullMeasured - zeroMeasured));
		return corrected - NewMaxPercent;
	}

	public void ApplyZeroRecoveryStep(double minStepPercent, double repeatedFullDeltaPercent)
	{
		NewMinPercent = QuantizeOutputTarget(NewMinPercent + minStepPercent);
		NewMaxPercent = QuantizeOutputTarget(NewMaxPercent + repeatedFullDeltaPercent);
		NewMidPercent = (NewMinPercent + NewMaxPercent) / 2.0;
		ApplyBridgeDesiredTargets();
	}

	public void ApplyFullRecoveryStep(double maxStepPercent)
	{
		NewMaxPercent = QuantizeOutputTarget(NewMaxPercent + maxStepPercent);
		NewMidPercent = (NewMinPercent + NewMaxPercent) / 2.0;
		ApplyBridgeDesiredTargets();
	}

	private static double QuantizeOutputTarget(double value)
	{
		return Math.Round(value, 2, MidpointRounding.ToEven);
	}

	private static double AverageNearTarget(double[] source, double target)
	{
		return source
			.Where((double x) => Math.Abs(x - target) < 1.0)
			.DefaultIfEmpty(target)
			.Average();
	}

	private double OriginalOutputSpanPercent()
	{
		double[] source = OriginalBridgeDesiredPercent.Length > 0
			? OriginalBridgeDesiredPercent
			: BridgeDesiredPercent;
		if (source.Length == 0)
		{
			return 80.0;
		}
		double span = source.Max() - source.Min();
		return Math.Abs(span) < 0.0001 ? 80.0 : span;
	}

	private void ValidateOutputCorrectionInputs(double zeroMeasured, double fullMeasured)
	{
		if (double.IsNaN(zeroMeasured) || double.IsNaN(fullMeasured))
		{
			throw new InvalidOperationException("零点/满点电压无效，不能修正百分比。");
		}
		if (Math.Abs(fullMeasured - zeroMeasured) < 0.05)
		{
			throw new InvalidOperationException($"零点和满点电压差过小：Zero={zeroMeasured:0.######}V Full={fullMeasured:0.######}V，请检查压力、通道或产品输出。");
		}
	}

	private void ApplyBridgeDesiredTargets()
	{
		double[] array = ((OriginalBridgeDesiredPercent.Length == BridgeDesiredPercent.Length) ? OriginalBridgeDesiredPercent : BridgeDesiredPercent);
		for (int i = 0; i < BridgeDesiredPercent.Length; i++)
		{
			if (Math.Abs(array[i] - TargetPercentMin) < 0.0001)
			{
				BridgeDesiredPercent[i] = NewMinPercent;
			}
			else if (Math.Abs(array[i] - TargetPercentMid) < 0.0001)
			{
				BridgeDesiredPercent[i] = NewMidPercent;
			}
			else if (Math.Abs(array[i] - TargetPercentMax) < 0.0001)
			{
				BridgeDesiredPercent[i] = NewMaxPercent;
			}
		}
		Changed("NewMinPercent");
		Changed("NewMaxPercent");
		Changed("NewMidPercent");
	}

	public void CalculateCoefficients(bool preserveTempCoefficients)
	{
		int[] array = new int[10];
		int[] negCoeffs = new int[10];
		double[] bridgeDesired = BridgeDesiredPercent.Select(BridgePercentToCode).ToArray();
		double[] tempDesired = TempDesiredDeg.Select(TempDegreeToCode).ToArray();
		int num = CalibrationL6.CalculateCoefficients(array, negCoeffs, BridgeRaw.Length, 1023, 0, BridgeRaw, bridgeDesired, TempRaw, tempDesired);
		if (num != 0)
		{
			throw new InvalidOperationException("CalculateCoefficients ret=" + num);
		}
		if (preserveTempCoefficients && OriginalCoefficients.Length >= 10)
		{
			array[7] = OriginalCoefficients[7];
			array[8] = OriginalCoefficients[8];
			array[9] = OriginalCoefficients[9];
		}
		Coefficients = array;
		Changed("CoefficientsText");
	}

	public int VerifyCoefficients()
	{
		if (Coefficients.Length != 10)
		{
			throw new InvalidOperationException("系数数量不是10，无法验证。");
		}
		return CalibrationL6.VerifyCoefficients(Coefficients);
	}

	public void EnsureCoefficientsValid()
	{
		int result = VerifyCoefficients();
		if (result != 0)
		{
			throw new InvalidOperationException($"系数结果超差，未写入（VerifyCoefficients ret={result}）");
		}
	}

	public static double Linearity(double measuredMid, double zero, double full)
	{
		double num = full - zero;
		if (Math.Abs(num) < 1E-12)
		{
			return double.NaN;
		}
		return Math.Abs(measuredMid - (zero + full) / 2.0) / num * 100.0;
	}

	private static double BridgePercentToCode(double percent)
	{
		return percent * 16777215.0 / 100.0;
	}

	private static double TempDegreeToCode(double degrees)
	{
		return (degrees + 1.0) * 16777215.0 / 66.0;
	}

	private void Changed(string n)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
	}
}
