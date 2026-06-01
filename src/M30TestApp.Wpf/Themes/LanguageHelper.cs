using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Themes;

public static class LanguageHelper
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    public static void Apply(string? language)
    {
        var normalized = Normalize(language);
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = normalized == Chinese
                    ? new Uri("pack://application:,,,/Strings/zh-CN.xaml")
                    : new Uri("pack://application:,,,/Strings/en-US.xaml")
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source?.OriginalString.Contains("/Strings/", StringComparison.OrdinalIgnoreCase) == true)
                    merged.RemoveAt(i);
            }
            merged.Add(dict);
        });
    }

    public static void ApplyFromSettings(IniFile ini) => Apply(AppPreferences.Language(ini));

    public static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Chinese;
}
