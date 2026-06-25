using System.Collections.Generic;
using System.Globalization;

namespace M30TestApp.Core.Config;

/// <summary>
/// 鍘嬪姏绫诲瀷锛氱粷鍘嬶紙Absolute锛夈€佽〃鍘嬶紙Gauge锛夈€佸樊鍘嬶紙Differential锛夈€?
/// 瀵瑰簲鍘嬪姏鎺у埗鍣ㄧ殑 SetAbs / SetGaug / SetDiff 鍛戒护銆?
/// </summary>
public enum PressureType
{
    /// <summary>琛ㄥ帇锛圙auge锛夆€斺€?鐩稿澶ф皵鍘嬶紝榛樿鍊笺€?/summary>
    Gauge,
    /// <summary>缁濆帇锛圓bsolute锛夆€斺€?鐩稿鐪熺┖闆剁偣銆?/summary>
    Absolute,
    /// <summary>宸帇锛圖ifferential锛夆€斺€?涓よ矾鍘嬪姏涔嬪樊銆?/summary>
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
    /// 鏂规鎵€鍦ㄧ殑鏂囦欢澶瑰悕锛堝 "M30娴嬭瘯"锛夈€傜敤浜庡垽鏂鍑烘牸寮忋€?
    /// </summary>
    public string FolderName { get; set; } = "";
    /// <summary>
    /// 鏂规绾ч粯璁ゅ帇鍔涚被鍨嬨€傚綋鍘嬪姏鐐规湭鍗曠嫭鎸囧畾绫诲瀷鏃朵娇鐢ㄦ鍊笺€?
    /// </summary>
    public PressureType DefaultPressureType { get; set; } = PressureType.Gauge;
    public List<TempPoint> TempPoints { get; } = new();
    public List<PressurePoint> PressurePoints { get; } = new();
    /// <summary>The task script, pipe-separated commands (ASLab style).</summary>
    public string TaskScript { get; set; } = "";
    /// <summary>Performance specification limits (Min/Max) for pass/fail judgment.</summary>
    public SpecLimits Specs { get; set; } = new();

    /// <summary>鎺㈡紡鍘嬪姏鐐逛笌娉勬紡鐜囬槇鍊硷紱鐣欑┖鏃舵寜鍘嬪姏绫诲瀷鑷姩鎺ㄥ銆?/summary>
    public LeakCheckSettings LeakCheck { get; set; } = new();

    /// <summary>Metric codes enabled for pass/fail judgment (empty = all enabled).</summary>
    public Dictionary<string, bool> EnabledMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static TestPlan Load(string path)
    {
        var ini = IniFile.Load(path);

        // 瑙ｆ瀽鏂规绾ч粯璁ゅ帇鍔涚被鍨?
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
                // 璇诲彇姣忎釜鍘嬪姏鐐圭殑鐙珛绫诲瀷锛屾湭鎸囧畾鍒欑户鎵挎柟妗堥粯璁ゅ€?
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
            // 鍙湁褰撳帇鍔涚偣绫诲瀷涓庢柟妗堥粯璁ゅ€间笉鍚屾椂鎵嶅崟鐙繚瀛橈紝鍑忓皯鍐椾綑
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

    /// <summary>灏嗗瓧绗︿覆锛堣嫳鏂囨垨涓枃锛夎В鏋愪负 <see cref="PressureType"/>锛屾棤娉曡瘑鍒椂杩斿洖 Gauge銆?/summary>
    public static PressureType ParsePressureType(string text) => text.Trim() switch
    {
        "Absolute" or "absolute" or "ABS" or "abs" or "绝压" or "缁濆帇" => PressureType.Absolute,
        "Differential" or "differential" or "DIFF" or "diff" or "差压" or "宸帇" => PressureType.Differential,
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
    /// <summary>姝ゅ帇鍔涚偣鐨勫帇鍔涚被鍨嬶紙缁濆帇/琛ㄥ帇/宸帇锛夈€?/summary>
    public PressureType PressureType { get; set; } = PressureType.Gauge;

    /// <summary>鍘嬪姏绫诲瀷鐨勪腑鏂囨樉绀哄悕绉帮紝鐢ㄤ簬 UI 缁戝畾銆?/summary>
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
            "Absolute" or "absolute" or "ABS" or "abs" or "绝压" or "缁濆帇" => PressureType.Absolute,
            "Differential" or "differential" or "DIFF" or "diff" or "差压" or "宸帇" => PressureType.Differential,
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

    public SpecLimits()
    {
        ApplyDefaults();
    }

    private static readonly string[] Names =
        { "Offset", "Span", "Linearity", "TCO", "TCS", "TCR", "THO", "THS", "PressureHysteresis", "CT" };

    public SpecRange this[string name] => name switch
    {
        "Offset" => Offset,
        "Span" => Span,
        "Linearity" or "NL" => Linearity,
        "TCO" => TCO,
        "TCS" => TCS,
        "TCR" => TCR,
        "THO" => THO,
        "THS" => THS,
        "PressureHysteresis" or "PH" => PressureHysteresis,
        "CT" or "TCT" => CT,
        _ => new SpecRange()
    };

    public static SpecLimits LoadFrom(IniFile ini)
    {
        var s = new SpecLimits();
        foreach (var n in Names)
        {
            var r = s[n];
            r.Min = DefaultOr(GetSpecValue(ini, n, "Min"), r.Min);
            r.Max = DefaultOr(GetSpecValue(ini, n, "Max"), r.Max);
        }
        return s;
    }

    private static string GetSpecValue(IniFile ini, string name, string bound)
    {
        var canonical = ini.Get("Specs", $"{name}.{bound}");
        if (!string.IsNullOrWhiteSpace(canonical)) return canonical;

        var alias = name switch
        {
            "Linearity" => "NL",
            "PressureHysteresis" => "PH",
            "CT" => "TCT",
            _ => "",
        };
        return string.IsNullOrWhiteSpace(alias) ? "" : ini.Get("Specs", $"{alias}.{bound}");
    }

    private void ApplyDefaults()
    {
        Offset.Min = "-25";
        Offset.Max = "25";
        Span.Min = "60";
        Span.Max = "140";
        Linearity.Min = "-0.3";
        Linearity.Max = "0.3";
        TCO.Min = "-0.05";
        TCO.Max = "0.05";
        TCS.Min = "-0.21";
        TCS.Max = "-0.17";
        TCR.Min = "0.07";
        TCR.Max = "0.11";
        THO.Min = "-2";
        THO.Max = "2";
        THS.Min = "-2";
        THS.Max = "2";
        PressureHysteresis.Min = "-0.3";
        PressureHysteresis.Max = "0.3";
        CT.Min = "0.145";
        CT.Max = "0.159";
    }

    private static string DefaultOr(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

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
