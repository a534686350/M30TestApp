using System.IO;

namespace M30TestApp.Core.Common;

public static class AppPaths
{
    public static string BaseDir { get; set; } =
        Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? ".";

    public static string SettingDir => Path.Combine(BaseDir, "setting");
    public static string TestConfigDir => Path.Combine(SettingDir, "TestConfig");
    public static string SupportDir => Path.Combine(BaseDir, "support");
    public static string DataDir => Path.Combine(BaseDir, "data");
    public static string LogDir => Path.Combine(BaseDir, "log");

    public static string CommandIni => Path.Combine(SettingDir, "Command.ini");
    public static string SettingIni => Path.Combine(SettingDir, "Setting.ini");
    public static string ConfigIni => Path.Combine(SettingDir, "Config.ini");
    public static string SlotCsv => Path.Combine(SettingDir, "工位对应表.csv");

    public static void EnsureDirs()
    {
        foreach (var d in new[] { SettingDir, TestConfigDir, SupportDir, DataDir, LogDir })
            Directory.CreateDirectory(d);
    }
}
