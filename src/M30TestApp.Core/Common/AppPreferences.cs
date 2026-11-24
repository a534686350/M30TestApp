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

    public static string Theme(IniFile ini) => Get(ini, "Theme", "Dark");

    public static string Language(IniFile ini) => Get(ini, "Language", "zh-CN");

    public static int LogRetainDays(IniFile ini) => GetInt(ini, "LogRetainDays", 30);

    public static bool AutoLoadLastPlan(IniFile ini) => GetBool(ini, "AutoLoadLastPlan", true);

    public static bool AutoExportCsv(IniFile ini) => GetBool(ini, "AutoExportCsv", true);

    /// <summary>
    /// 异常退出/取消时是否保留断点（续测）。
    ///
    /// 默认 **开** —— 现场断电、误关程序、仪器掉线都很常见，默认保留断点才能让
    /// 「程序打开时提示是否继续」这条路径真正跑得起来；不需要的可以在配置页关掉。
    /// </summary>
    public static bool SaveCheckpointOnAbort(IniFile ini) => GetBool(ini, "SaveCheckpointOnAbort", true);

    /// <summary>
    /// 设备连不上时是否自动换 SIM 模拟跑。
    ///
    /// 默认 **开** —— 现场仪器没上电 / 线没插 / 串口被占 / VISA 驱动没装都很常见，
    /// 这时候整条流程（方案 → 工位 → 保温 → 控压 → 采集 → 判定 → 导出）照样能跑通，
    /// 只是数据是模拟的；真机接好后重新开一次程序即可。
    /// </summary>
    /// <summary>兼容旧配置键；设备后端现在由管理员在界面手动切换，不再失败自动转 SIM。</summary>
    public static bool FallbackSimOnDisconnect(IniFile ini) => GetBool(ini, "FallbackSimOnDisconnect", false);

    /// <summary>是否阻止 Windows 自动更新与自动重启，避免长时间测试被打断。</summary>
    public static bool BlockSystemUpdate(IniFile ini) => GetBool(ini, "BlockSystemUpdate", false);

    public static bool DebugMode(IniFile ini) => GetBool(ini, "DebugMode", false);

    public static string LastPlan(IniFile ini) => Get(ini, "LastPlan", "");

    public static string LastPlanFolder(IniFile ini) => Get(ini, "LastPlanFolder", "");

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
