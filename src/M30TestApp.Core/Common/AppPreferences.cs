using M30TestApp.Core.Config;

namespace M30TestApp.Core.Common;

/// <summary>Reads/writes application preferences stored in <c>Setting.ini</c> [App] section.</summary>
public static class AppPreferences
{
    public const string Section = "App";

    public static string Get(IniFile ini, string key, string fallback = "") =>
        ini.Get(Section, key, fallback);

    public static bool GetBool(IniFile ini, string key, bool fallback = false)
    {
        var v = Get(ini, key, fallback ? "1" : "0");
        return v is "1" or "true" or "True" or "yes" or "Yes";
    }

    public static int GetInt(IniFile ini, string key, int fallback)
    {
        var v = Get(ini, key, fallback.ToString());
        return int.TryParse(v, out var n) ? n : fallback;
    }

    public static void Set(IniFile ini, string key, string value) => ini.Set(Section, key, value);

    public static void SetBool(IniFile ini, string key, bool value) => Set(ini, key, value ? "1" : "0");

    public static string Theme(IniFile ini) => Get(ini, "Theme", "Light");

    public static string Language(IniFile ini) => Get(ini, "Language", "zh-CN");

    public static int LogRetainDays(IniFile ini) => GetInt(ini, "LogRetainDays", 30);

    public static bool AutoLoadLastPlan(IniFile ini) => GetBool(ini, "AutoLoadLastPlan", true);

    public static bool AutoExportCsv(IniFile ini) => GetBool(ini, "AutoExportCsv", true);

    public static bool SaveCheckpointOnAbort(IniFile ini) => GetBool(ini, "SaveCheckpointOnAbort", false);

    public static bool FallbackSimOnDisconnect(IniFile ini) => GetBool(ini, "FallbackSimOnDisconnect", false);

    public static bool DebugMode(IniFile ini) => GetBool(ini, "DebugMode", false);

    public static string LastPlan(IniFile ini) => Get(ini, "LastPlan", "");

    public static void PruneOldLogs(IniFile ini)
    {
        var days = LogRetainDays(ini);
        if (days <= 0 || !Directory.Exists(AppPaths.LogDir)) return;
        var cutoff = DateTime.Now.AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch { /* best effort */ }
        }
    }
}
