using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace M30TestApp.Core.Config;

/// <summary>方案级探漏参数，保存在 TestConfig/*.ini 的 [LeakCheck] 段。</summary>
public sealed class LeakCheckSettings
{
    /// <summary>显式配置的探漏压力点；为空时按压力类型自动推导。</summary>
    public List<float> Pressures { get; } = new();

    /// <summary>探漏加压精度与泄漏率阈值；为空时使用 <see cref="TestPlan.Precision"/>。</summary>
    public float? Precision { get; set; }

    public static LeakCheckSettings LoadFrom(IniFile ini)
    {
        var settings = new LeakCheckSettings();
        var pressuresText = ini.Get("LeakCheck", "Pressures", "");
        if (!string.IsNullOrWhiteSpace(pressuresText))
        {
            foreach (var part in pressuresText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var pressure))
                    settings.Pressures.Add(pressure);
            }
        }

        var precisionText = ini.Get("LeakCheck", "Precision", "");
        if (float.TryParse(precisionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var precision))
            settings.Precision = precision;

        return settings;
    }

    public void SaveTo(IniFile ini)
    {
        if (Pressures.Count > 0)
        {
            ini.Set("LeakCheck", "Pressures",
                string.Join(",", Pressures.Select(p => p.ToString(CultureInfo.InvariantCulture))));
        }

        if (Precision.HasValue)
        {
            ini.Set("LeakCheck", "Precision",
                Precision.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}

public static class LeakCheckPlanHelper
{
    public const float AbsoluteDefaultLowPressure = 5f;

    public static float ResolveFullScale(TestPlan plan) =>
        plan.PressurePoints.Count > 0 ? plan.PressurePoints.Max(p => p.Value) : 0f;

    public static float ResolvePrecision(TestPlan plan) => plan.LeakCheck.Precision ?? plan.Precision;

    public static IReadOnlyList<float> ResolvePressures(TestPlan plan)
    {
        if (plan.LeakCheck.Pressures.Count > 0)
            return plan.LeakCheck.Pressures;

        var fullScale = ResolveFullScale(plan);
        if (fullScale <= 0)
            return Array.Empty<float>();

        if (plan.DefaultPressureType == PressureType.Absolute)
        {
            if (Math.Abs(fullScale - AbsoluteDefaultLowPressure) < 1e-4f)
                return new[] { fullScale };
            return new[] { AbsoluteDefaultLowPressure, fullScale };
        }

        return new[] { fullScale };
    }

    public static string DescribeDefaultPressures(TestPlan plan)
    {
        if (plan.LeakCheck.Pressures.Count > 0)
            return "已自定义";

        return plan.DefaultPressureType switch
        {
            PressureType.Absolute => $"默认 {AbsoluteDefaultLowPressure} 与满量程",
            PressureType.Differential => "默认满量程（差压）",
            _ => "默认满量程（表压）",
        };
    }
}
