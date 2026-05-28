using System;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Themes;

public static class ThemeHelper
{
    public static void Apply(string theme)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = theme == "Dark"
                    ? new Uri("pack://application:,,,/Themes/Dark.xaml")
                    : new Uri("pack://application:,,,/Themes/Light.xaml")
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true)
                    merged.RemoveAt(i);
            }
            merged.Insert(0, dict);
        });
    }

    public static void ApplyFromSettings(IniFile ini) => Apply(AppPreferences.Theme(ini));

    public static string ToDisplayName(string theme) => theme == "Dark" ? "暗色" : "亮色";

    public static string FromDisplayName(string display) =>
        display.StartsWith("暗", StringComparison.Ordinal) ? "Dark" : "Light";
}
