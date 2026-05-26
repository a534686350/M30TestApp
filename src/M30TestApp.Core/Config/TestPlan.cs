using System.Collections.Generic;
using System.Globalization;

namespace M30TestApp.Core.Config;

/// <summary>
/// A test plan loaded from setting/TestConfig/*.ini.
/// Mirrors ASLab semantics: a TestTaskPoint script plus temperature & pressure points.
/// </summary>
public sealed class TestPlan
{
    public string Name { get; set; } = "";
    public string SensorType { get; set; } = "";
    public string PressureUnit { get; set; } = "kPa";
    public float Precision { get; set; } = 0.05f;
    public List<TempPoint> TempPoints { get; } = new();
    public List<PressurePoint> PressurePoints { get; } = new();
    /// <summary>The task script, pipe-separated commands (ASLab style).</summary>
    public string TaskScript { get; set; } = "";
    /// <summary>Performance specification limits (Min/Max) for pass/fail judgment.</summary>
    public SpecLimits Specs { get; set; } = new();

    public static TestPlan Load(string path)
    {
        var ini = IniFile.Load(path);
        var plan = new TestPlan
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            SensorType = ini.Get("Plan", "SensorType"),
            PressureUnit = ini.Get("Plan", "PressureUnit", "kPa"),
            Precision = float.TryParse(ini.Get("Plan", "Precision", "0.05"), out var pr) ? pr : 0.05f,
            TaskScript = ini.Get("Plan", "TaskScript"),
        };

        foreach (var kv in ini["TempPoints"])
        {
            if (float.TryParse(kv.Value, out var v))
            {
                var soakText = ini.Get("TempPointSoakMinutes", kv.Key);
                int? soak = int.TryParse(soakText, out var s) ? s : null;
                plan.TempPoints.Add(new TempPoint(kv.Key, v, soak));
            }
        }
        foreach (var kv in ini["PressurePoints"])
        {
            if (float.TryParse(kv.Value, out var v))
                plan.PressurePoints.Add(new PressurePoint(kv.Key, v));
        }
        plan.Specs = SpecLimits.LoadFrom(ini);
        return plan;
    }

    /// <summary>Persist this plan to <paramref name="path"/> in the same INI layout as <see cref="Load"/>.</summary>
    public void Save(string path)
    {
        var ini = new IniFile();
        ini.Set("Plan", "SensorType", SensorType);
        ini.Set("Plan", "PressureUnit", PressureUnit);
        ini.Set("Plan", "Precision", Precision.ToString(CultureInfo.InvariantCulture));
        ini.Set("Plan", "TaskScript", TaskScript);
        foreach (var tp in TempPoints)
        {
            ini.Set("TempPoints", tp.Name, tp.Celsius.ToString(CultureInfo.InvariantCulture));
            if (tp.SoakMinutes.HasValue)
                ini.Set("TempPointSoakMinutes", tp.Name, tp.SoakMinutes.Value.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var pp in PressurePoints)
            ini.Set("PressurePoints", pp.Name, pp.Value.ToString(CultureInfo.InvariantCulture));
        Specs.SaveTo(ini);
        ini.Save(path);
    }
}

public sealed class TempPoint
{
    public TempPoint() { }
    public TempPoint(string name, float celsius, int? soakMinutes = null) { Name = name; Celsius = celsius; SoakMinutes = soakMinutes; }
    public string Name { get; set; } = "";
    public float Celsius { get; set; }
    public int? SoakMinutes { get; set; }
    public string SoakMinutesText
    {
        get => SoakMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => SoakMinutes = int.TryParse(value, out var minutes) ? minutes : null;
    }
}

public sealed class PressurePoint
{
    public PressurePoint() { }
    public PressurePoint(string name, float value) { Name = name; Value = value; }
    public string Name { get; set; } = "";
    public float Value { get; set; }
}

/// <summary>Min/Max range for a single spec metric. Empty string means no limit.</summary>
public sealed class SpecRange
{
    public string Min { get; set; } = "";
    public string Max { get; set; } = "";
    public double? MinVal => double.TryParse(Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    public double? MaxVal => double.TryParse(Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    public bool IsInRange(double value)
    {
        if (double.IsNaN(value)) return false;
        if (MinVal.HasValue && value < MinVal.Value) return false;
        if (MaxVal.HasValue && value > MaxVal.Value) return false;
        return true;
    }
    public bool HasLimits => MinVal.HasValue || MaxVal.HasValue;
}

/// <summary>Performance specification limits for all metrics.</summary>
public sealed class SpecLimits
{
    public SpecRange Offset { get; set; } = new();
    public SpecRange Span { get; set; } = new();
    public SpecRange Linearity { get; set; } = new();
    public SpecRange TCO { get; set; } = new();
    public SpecRange TCS { get; set; } = new();
    public SpecRange TCR { get; set; } = new();
    public SpecRange THO { get; set; } = new();
    public SpecRange THS { get; set; } = new();
    public SpecRange PressureHysteresis { get; set; } = new();
    public SpecRange CT { get; set; } = new();

    private static readonly string[] Names =
        { "Offset", "Span", "Linearity", "TCO", "TCS", "TCR", "THO", "THS", "PressureHysteresis", "CT" };

    public SpecRange this[string name] => name switch
    {
        "Offset" => Offset,
        "Span" => Span,
        "Linearity" => Linearity,
        "TCO" => TCO,
        "TCS" => TCS,
        "TCR" => TCR,
        "THO" => THO,
        "THS" => THS,
        "PressureHysteresis" => PressureHysteresis,
        "CT" => CT,
        _ => new SpecRange()
    };

    public static SpecLimits LoadFrom(IniFile ini)
    {
        var s = new SpecLimits();
        foreach (var n in Names)
        {
            var r = s[n];
            r.Min = ini.Get("Specs", $"{n}.Min");
            r.Max = ini.Get("Specs", $"{n}.Max");
        }
        return s;
    }

    public void SaveTo(IniFile ini)
    {
        foreach (var n in Names)
        {
            var r = this[n];
            if (!string.IsNullOrWhiteSpace(r.Min)) ini.Set("Specs", $"{n}.Min", r.Min);
            if (!string.IsNullOrWhiteSpace(r.Max)) ini.Set("Specs", $"{n}.Max", r.Max);
        }
    }
}
