using System.Collections.Generic;
using System.Globalization;

namespace M30TestApp.Core.Config;

/// <summary>
/// 压力类型：绝压（Absolute）、表压（Gauge）、差压（Differential）。
/// 对应压力控制器的 SetAbs / SetGaug / SetDiff 命令。
/// </summary>
public enum PressureType
{
    /// <summary>表压（Gauge）—— 相对大气压，默认值。</summary>
    Gauge,
    /// <summary>绝压（Absolute）—— 相对真空零点。</summary>
    Absolute,
    /// <summary>差压（Differential）—— 两路压力之差。</summary>
    Differential,
}

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
    /// <summary>
    /// 方案所在的文件夹名（如 "M30测试"）。用于判断导出格式。
    /// </summary>
    public string FolderName { get; set; } = "";
    /// <summary>
    /// 方案级默认压力类型。当压力点未单独指定类型时使用此值。
    /// </summary>
    public PressureType DefaultPressureType { get; set; } = PressureType.Gauge;
    public List<TempPoint> TempPoints { get; } = new();
    public List<PressurePoint> PressurePoints { get; } = new();
    /// <summary>The task script, pipe-separated commands (ASLab style).</summary>
    public string TaskScript { get; set; } = "";
    /// <summary>Performance specification limits (Min/Max) for pass/fail judgment.</summary>
    public SpecLimits Specs { get; set; } = new();

    /// <summary>探漏压力点与泄漏率阈值；留空时按压力类型自动推导。</summary>
    public LeakCheckSettings LeakCheck { get; set; } = new();

    /// <summary>Metric codes enabled for pass/fail judgment (empty = all enabled).</summary>
    public Dictionary<string, bool> EnabledMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static TestPlan Load(string path)
    {
        var ini = IniFile.Load(path);

        // 解析方案级默认压力类型
        var defaultPtText = ini.Get("Plan", "PressureType", "Gauge");
        var defaultPt = ParsePressureType(defaultPtText);

        var plan = new TestPlan
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            FolderName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? ""),
            SensorType = ini.Get("Plan", "SensorType"),
            PressureUnit = ini.Get("Plan", "PressureUnit", "kPa"),
            Precision = float.TryParse(ini.Get("Plan", "Precision", "0.05"), out var pr) ? pr : 0.05f,
            DefaultPressureType = defaultPt,
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
            {
                // 读取每个压力点的独立类型，未指定则继承方案默认值
                var ptText = ini.Get("PressurePointTypes", kv.Key, "");
                var pt = string.IsNullOrWhiteSpace(ptText) ? defaultPt : ParsePressureType(ptText);
                plan.PressurePoints.Add(new PressurePoint(kv.Key, v, pt));
            }
        }
        plan.Specs = SpecLimits.LoadFrom(ini);
        plan.LeakCheck = LeakCheckSettings.LoadFrom(ini);
        plan.EnabledMetrics.Clear();
        foreach (var kv in ini["Metrics"])
        {
            var on = kv.Value is "1" or "true" or "True" or "yes";
            plan.EnabledMetrics[kv.Key] = on;
        }
        return plan;
    }

    /// <summary>Persist this plan to <paramref name="path"/> in the same INI layout as <see cref="Load"/>.</summary>
    public void Save(string path)
    {
        var ini = new IniFile();
        ini.Set("Plan", "SensorType", SensorType);
        ini.Set("Plan", "PressureUnit", PressureUnit);
        ini.Set("Plan", "Precision", Precision.ToString(CultureInfo.InvariantCulture));
        ini.Set("Plan", "PressureType", DefaultPressureType.ToString());
        ini.Set("Plan", "TaskScript", TaskScript);
        foreach (var tp in TempPoints)
        {
            ini.Set("TempPoints", tp.Name, tp.Celsius.ToString(CultureInfo.InvariantCulture));
            if (tp.SoakMinutes.HasValue)
                ini.Set("TempPointSoakMinutes", tp.Name, tp.SoakMinutes.Value.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var pp in PressurePoints)
        {
            ini.Set("PressurePoints", pp.Name, pp.Value.ToString(CultureInfo.InvariantCulture));
            // 只有当压力点类型与方案默认值不同时才单独保存，减少冗余
            if (pp.PressureType != DefaultPressureType)
                ini.Set("PressurePointTypes", pp.Name, pp.PressureType.ToString());
        }
        Specs.SaveTo(ini);
        LeakCheck.SaveTo(ini);
        foreach (var kv in EnabledMetrics)
            ini.Set("Metrics", kv.Key, kv.Value ? "1" : "0");
        ini.Save(path);
    }

    public bool IsMetricEnabled(string code)
    {
        if (EnabledMetrics.Count == 0) return true;
        return EnabledMetrics.TryGetValue(code, out var on) && on;
    }

    /// <summary>将字符串（英文或中文）解析为 <see cref="PressureType"/>，无法识别时返回 Gauge。</summary>
    public static PressureType ParsePressureType(string text) => text.Trim() switch
    {
        "Absolute" or "absolute" or "ABS" or "abs" or "绝压" => PressureType.Absolute,
        "Differential" or "differential" or "DIFF" or "diff" or "差压" => PressureType.Differential,
        _ => PressureType.Gauge,
    };
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
    public PressurePoint(string name, float value, PressureType pressureType = PressureType.Gauge)
    {
        Name = name;
        Value = value;
        PressureType = pressureType;
    }
    public string Name { get; set; } = "";
    public float Value { get; set; }
    /// <summary>此压力点的压力类型（绝压/表压/差压）。</summary>
    public PressureType PressureType { get; set; } = PressureType.Gauge;

    /// <summary>压力类型的中文显示名称，用于 UI 绑定。</summary>
    public string PressureTypeDisplay
    {
        get => PressureType switch
        {
            PressureType.Absolute     => "绝压",
            PressureType.Gauge        => "表压",
            PressureType.Differential => "差压",
            _                         => "表压",
        };
        set => PressureType = value switch
        {
            "绝压" => PressureType.Absolute,
            "差压" => PressureType.Differential,
            _      => PressureType.Gauge,
        };
    }
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
