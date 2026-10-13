using System;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Themes;

public static class ThemeHelper
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static void Apply(string theme)
    {
        theme = Normalize(theme);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = theme == Dark
                    ? new Uri("pack://application:,,,/Themes/Dark.xaml")
                    : new Uri("pack://application:,,,/Themes/Light.xaml")
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (IsThemeDictionary(merged[i])) merged.RemoveAt(i);
            }
            merged.Insert(0, dict);
        });
    }

    /// <summary>
    /// 是否是主题字典（Light / Dark）。
    ///
    /// 这里**不能**用 <c>Contains("/Themes/")</c> 判断 —— App.xaml 里写的是相对路径
    /// <c>Source="Themes/Light.xaml"</c>（没有前导斜杠），而代码插入的 Dark 用的是
    /// <c>pack://application:,,,/Themes/Dark.xaml</c>（有斜杠）。用带斜杠的写法匹配，
    /// App.xaml 里那份 Light 永远删不掉，它会排在后面、优先级更高，
    /// 于是「切了主题却没生效」（内容区始终是浅色）。改判文件名并统一斜杠方向。
    /// </summary>
    private static bool IsThemeDictionary(ResourceDictionary d)
    {
        var src = d.Source?.OriginalString;
        if (string.IsNullOrEmpty(src)) return false;
        src = src.Replace('\\', '/');
        return src.Contains("themes/light.xaml", StringComparison.OrdinalIgnoreCase)
            || src.Contains("themes/dark.xaml", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyFromSettings(IniFile ini) => Apply(AppPreferences.Theme(ini));

    public static string Normalize(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static string ToDisplayName(string theme) => Normalize(theme) == Dark ? "深色" : "浅色";

    public static string FromDisplayName(string display) =>
        display.StartsWith("深", StringComparison.Ordinal) ? Dark : Light;
}
