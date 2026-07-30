using System;
using System.Collections.Generic;
using System.Linq;

namespace F40MultiCalibrator;

internal sealed record F40NamedMetric(string Label, double Value);

internal sealed record F40ThermalMetric(string Label, double OffsetPercentFs, double SpanPercentFs);

internal sealed record F40TestMetricsResult(
	double OffsetV,
	double SpanV,
	double PressureHysteresisPercentFs,
	IReadOnlyList<F40NamedMetric> NonLinearityByTemperature,
	double MaxNonLinearityPercentFs,
	IReadOnlyList<F40ThermalMetric> ThermalHysteresis,
	IReadOnlyList<F40ThermalMetric> ThermalCoefficients,
	double TotalOffsetPercentFs,
	double TotalSpanPercentFs,
	double MaxAccuracyErrorV,
	double MaxAccuracyPercentFs,
	int SampleCount,
	int ExpectedSampleCount)
{
	public bool IsComplete => SampleCount == ExpectedSampleCount;
}

internal static class F40TestMetricsCalculator
{
	public static F40TestMetricsResult Calculate(
		IReadOnlyList<double> temperatures,
		IReadOnlyList<double> pressures,
		double[,] voltages,
		double pressureZero,
		double pressureFull,
		double outputMinV,
		double outputMaxV)
	{
		if (temperatures.Count == 0 || pressures.Count == 0)
		{
			return Empty(temperatures.Count * pressures.Count);
		}
		if (voltages.GetLength(0) != temperatures.Count || voltages.GetLength(1) != pressures.Count)
		{
			throw new ArgumentException("Voltage matrix dimensions do not match the test plan.", nameof(voltages));
		}

		int fullIndex = ClosestPressureIndex(pressures, pressureFull, 0, pressures.Count - 1);
		int zeroStartIndex = ClosestPressureIndex(pressures, pressureZero, 0, fullIndex);
		int zeroEndIndex = ClosestPressureIndex(pressures, pressureZero, fullIndex, pressures.Count - 1);
		double offset = voltages[0, zeroStartIndex];
		double span = Difference(voltages[0, fullIndex], offset);
		double denominator = Math.Abs(span) > 1E-12 ? span : double.NaN;
		double pho = DividePercent(Difference(voltages[0, zeroEndIndex], offset), denominator);

		List<int> calculationTemperatures = CalculationTemperatureIndices(temperatures);
		Dictionary<int, double> nonLinearityByIndex = new();
		for (int tempIndex = 0; tempIndex < temperatures.Count; tempIndex++)
		{
			nonLinearityByIndex[tempIndex] = CalculateNonLinearity(
				pressures,
				voltages,
				tempIndex,
				zeroStartIndex,
				fullIndex,
				zeroEndIndex,
				pressureZero,
				pressureFull,
				denominator);
		}
		List<F40NamedMetric> nonLinearity = new();
		foreach (int tempIndex in calculationTemperatures)
		{
			nonLinearity.Add(new F40NamedMetric(FormatTemperature(temperatures[tempIndex]), nonLinearityByIndex[tempIndex]));
		}

		List<F40ThermalMetric> hysteresis = new();
		foreach ((int From, int To, string Label) pair in ThermalHysteresisPairs(temperatures, calculationTemperatures))
		{
			double offsetChange = DividePercent(
				Difference(voltages[pair.To, zeroStartIndex], voltages[pair.From, zeroStartIndex]),
				denominator);
			double spanChange = DividePercent(
				Difference(SpanAt(voltages, pair.To, zeroStartIndex, fullIndex), SpanAt(voltages, pair.From, zeroStartIndex, fullIndex)),
				denominator);
			hysteresis.Add(new F40ThermalMetric(pair.Label, offsetChange, spanChange));
		}

		List<F40ThermalMetric> coefficients = new();
		for (int i = 1; i < calculationTemperatures.Count; i++)
		{
			int from = calculationTemperatures[i - 1];
			int to = calculationTemperatures[i];
			double deltaTemperature = temperatures[to] - temperatures[from];
			double offsetCoefficient = Divide(
				DividePercent(Difference(voltages[to, zeroStartIndex], voltages[from, zeroStartIndex]), denominator),
				deltaTemperature);
			double spanCoefficient = Divide(
				DividePercent(Difference(SpanAt(voltages, to, zeroStartIndex, fullIndex), SpanAt(voltages, from, zeroStartIndex, fullIndex)), denominator),
				deltaTemperature);
			coefficients.Add(new F40ThermalMetric(
				$"{FormatTemperature(temperatures[from])}->{FormatTemperature(temperatures[to])}",
				offsetCoefficient,
				spanCoefficient));
		}

		double totalOffset = double.NaN;
		double totalSpan = double.NaN;
		if (calculationTemperatures.Count > 0)
		{
			int first = calculationTemperatures[0];
			int last = calculationTemperatures[^1];
			totalOffset = DividePercent(Difference(voltages[last, zeroStartIndex], voltages[first, zeroStartIndex]), denominator);
			totalSpan = DividePercent(
				Difference(SpanAt(voltages, last, zeroStartIndex, fullIndex), SpanAt(voltages, first, zeroStartIndex, fullIndex)),
				denominator);
		}

		double nominalSpan = outputMaxV - outputMinV;
		List<double> accuracyErrorsV = new();
		int sampleCount = 0;
		for (int ti = 0; ti < temperatures.Count; ti++)
		{
			for (int pi = 0; pi < pressures.Count; pi++)
			{
				double measured = voltages[ti, pi];
				if (!IsFinite(measured))
				{
					continue;
				}
				sampleCount++;
				double ideal = IdealVoltage(pressures[pi], pressureZero, pressureFull, outputMinV, outputMaxV);
				if (IsFinite(ideal))
				{
					accuracyErrorsV.Add(measured - ideal);
				}
			}
		}
		double maxAccuracyErrorV = SignedLargestMagnitude(accuracyErrorsV);
		double maxAccuracyPercent = DividePercent(maxAccuracyErrorV, nominalSpan);

		return new F40TestMetricsResult(
			offset,
			span,
			pho,
			nonLinearity,
			SignedLargestMagnitude(nonLinearityByIndex.Values),
			hysteresis,
			coefficients,
			totalOffset,
			totalSpan,
			maxAccuracyErrorV,
			maxAccuracyPercent,
			sampleCount,
			temperatures.Count * pressures.Count);
	}

	private static double CalculateNonLinearity(
		IReadOnlyList<double> pressures,
		double[,] voltages,
		int temperatureIndex,
		int zeroStartIndex,
		int fullIndex,
		int zeroEndIndex,
		double pressureZero,
		double pressureFull,
		double denominator)
	{
		List<double> errors = new();
		AddBranchErrors(errors, pressures, voltages, temperatureIndex, zeroStartIndex, fullIndex, zeroStartIndex, pressureZero, pressureFull, denominator);
		AddBranchErrors(errors, pressures, voltages, temperatureIndex, fullIndex, zeroEndIndex, zeroEndIndex, pressureZero, pressureFull, denominator);
		return SignedLargestMagnitude(errors);
	}

	private static void AddBranchErrors(
		List<double> errors,
		IReadOnlyList<double> pressures,
		double[,] voltages,
		int temperatureIndex,
		int branchStart,
		int branchEnd,
		int zeroIndex,
		double pressureZero,
		double pressureFull,
		double denominator)
	{
		int low = Math.Min(branchStart, branchEnd);
		int high = Math.Max(branchStart, branchEnd);
		double zeroV = voltages[temperatureIndex, zeroIndex];
		double fullV = voltages[temperatureIndex, branchStart == zeroIndex ? branchEnd : branchStart];
		for (int index = low + 1; index < high; index++)
		{
			double fraction = Divide(pressures[index] - pressureZero, pressureFull - pressureZero);
			double measured = voltages[temperatureIndex, index];
			double ideal = zeroV + fraction * (fullV - zeroV);
			errors.Add(DividePercent(Difference(measured, ideal), denominator));
		}
	}

	private static IEnumerable<(int From, int To, string Label)> ThermalHysteresisPairs(
		IReadOnlyList<double> temperatures,
		IReadOnlyList<int> calculationTemperatures)
	{
		if (temperatures.Count == 5 && NearlyEqual(temperatures[0], temperatures[2]) && NearlyEqual(temperatures[2], temperatures[4]))
		{
			yield return (0, 2, $"{FormatTemperature(temperatures[0])}->{FormatTemperature(temperatures[1])}->{FormatTemperature(temperatures[2])}");
			yield return (2, 4, $"{FormatTemperature(temperatures[2])}->{FormatTemperature(temperatures[3])}->{FormatTemperature(temperatures[4])}");
			yield break;
		}
		for (int i = 1; i < calculationTemperatures.Count; i++)
		{
			int from = calculationTemperatures[i - 1];
			int to = calculationTemperatures[i];
			yield return (from, to, $"{FormatTemperature(temperatures[from])}->{FormatTemperature(temperatures[to])}");
		}
	}

	private static List<int> CalculationTemperatureIndices(IReadOnlyList<double> temperatures)
	{
		if (temperatures.Count == 5 && NearlyEqual(temperatures[0], temperatures[2]) && NearlyEqual(temperatures[2], temperatures[4]))
		{
			return new List<int> { 1, 2, 3 };
		}
		return Enumerable.Range(0, temperatures.Count).ToList();
	}

	private static int ClosestPressureIndex(IReadOnlyList<double> pressures, double target, int start, int end)
	{
		int best = Math.Clamp(start, 0, pressures.Count - 1);
		double bestDistance = Math.Abs(pressures[best] - target);
		for (int index = Math.Max(0, start); index <= Math.Min(end, pressures.Count - 1); index++)
		{
			double distance = Math.Abs(pressures[index] - target);
			if (distance < bestDistance)
			{
				best = index;
				bestDistance = distance;
			}
		}
		return best;
	}

	private static double SpanAt(double[,] values, int temperatureIndex, int zeroIndex, int fullIndex) =>
		Difference(values[temperatureIndex, fullIndex], values[temperatureIndex, zeroIndex]);

	private static double IdealVoltage(double pressure, double pressureZero, double pressureFull, double outputMinV, double outputMaxV)
	{
		double fraction = Divide(pressure - pressureZero, pressureFull - pressureZero);
		return IsFinite(fraction) ? outputMinV + fraction * (outputMaxV - outputMinV) : double.NaN;
	}

	private static double Difference(double left, double right) => IsFinite(left) && IsFinite(right) ? left - right : double.NaN;

	private static double Divide(double numerator, double denominator) =>
		IsFinite(numerator) && IsFinite(denominator) && Math.Abs(denominator) > 1E-12 ? numerator / denominator : double.NaN;

	private static double DividePercent(double numerator, double denominator) => Divide(numerator, denominator) * 100.0;

	private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1E-9;

	private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

	private static double SignedLargestMagnitude(IEnumerable<double> values)
	{
		double result = double.NaN;
		foreach (double value in values.Where(IsFinite))
		{
			if (double.IsNaN(result) || Math.Abs(value) > Math.Abs(result))
			{
				result = value;
			}
		}
		return result;
	}

	private static string FormatTemperature(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "C";

	private static F40TestMetricsResult Empty(int expectedSampleCount) => new(
		double.NaN,
		double.NaN,
		double.NaN,
		Array.Empty<F40NamedMetric>(),
		double.NaN,
		Array.Empty<F40ThermalMetric>(),
		Array.Empty<F40ThermalMetric>(),
		double.NaN,
		double.NaN,
		double.NaN,
		double.NaN,
		0,
		expectedSampleCount);
}
