using System;
using System.IO;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.Themes;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirs();
        AppLog.Configure(Path.Combine(AppPaths.LogDir, $"run-{DateTime.Now:yyyyMMdd}.log"));
        AppLog.Info("App", $"Startup. Base = {AppPaths.BaseDir}");

        // ── Global crash guards ────────────────────────────────────────────
        // SIM/HW failures inside async commands should always be surfaced as a
        // dialog, never tear down the dispatcher.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Dispatcher", args.Exception.ToString());
            MessageBox.Show(args.Exception.Message, "未处理异常",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Error("AppDomain", ex.ToString());
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Task", args.Exception.ToString());
            args.SetObserved();
        };

        // Load configuration. Falls back to in-memory defaults so the app launches
        // even on a fresh machine without setting/*.
        var commands = File.Exists(AppPaths.CommandIni)
            ? CommandDictionary.Load(AppPaths.CommandIni)
            : new CommandDictionary(new IniFile());

        var settingIni = File.Exists(AppPaths.SettingIni)
            ? IniFile.Load(AppPaths.SettingIni)
            : new IniFile();
        ThemeHelper.ApplyFromSettings(settingIni);
        LanguageHelper.ApplyFromSettings(settingIni);
        AppPreferences.PruneOldLogs(settingIni);
        var station = StationProfile.Load(settingIni);

        var slots = File.Exists(AppPaths.SlotCsv)
            ? SlotTable.Load(AppPaths.SlotCsv)
            : SlotTable.Load("");

        var planFile = Directory.Exists(AppPaths.TestConfigDir)
            ? Directory.GetFiles(AppPaths.TestConfigDir, "*.ini", SearchOption.AllDirectories) : Array.Empty<string>();
        TestPlan plan;
        var lastPlanName = AppPreferences.LastPlan(settingIni);
        // Search in root and all sub-folders for the last plan
        string? lastPlanPath = null;
        if (!string.IsNullOrWhiteSpace(lastPlanName))
        {
            var candidate = Path.Combine(AppPaths.TestConfigDir, lastPlanName + ".ini");
            if (File.Exists(candidate))
                lastPlanPath = candidate;
            else
            {
                // Search sub-folders
                foreach (var dir in Directory.GetDirectories(AppPaths.TestConfigDir))
                {
                    candidate = Path.Combine(dir, lastPlanName + ".ini");
                    if (File.Exists(candidate)) { lastPlanPath = candidate; break; }
                }
            }
        }
        if (AppPreferences.AutoLoadLastPlan(settingIni) && lastPlanPath is not null && File.Exists(lastPlanPath))
            plan = TestPlan.Load(lastPlanPath);
        else if (planFile.Length > 0)
            plan = TestPlan.Load(planFile[0]);
        else
            plan = new TestPlan
        {
            Name = "demo",
            SensorType = "M30-DEMO",
            PressureUnit = "kPa",
            Precision = 0.05f,
            TaskScript = "Initial:Pressure|Initial:Board|Initial:CommuTest|DAQ:ClearData|DAQ:TestType,测试|Read:Usource|Read:Isource|Read:Usign|Read:UT|TP:SetPressurePoint,1,TEST|Read:Usign|TP:SetPressurePoint,2,TEST|Read:Usign|TP:SetPressurePoint,3,TEST|Read:Usign|TP:Vent|Save:TestData|Cal:Test|DAQ:Down",
            };
        if (plan.PressurePoints.Count == 0)
        {
            plan.PressurePoints.Add(new PressurePoint("P1", 0));
            plan.PressurePoints.Add(new PressurePoint("P2", 50));
            plan.PressurePoints.Add(new PressurePoint("P3", 100));
        }
        if (plan.TempPoints.Count == 0)
        {
            plan.TempPoints.Add(new TempPoint("T1", 25));
        }
        // Hard cap on slots is 256. SIM seeds 32 by default; configurable via [Slots] Count.
        const int SlotMax = 256;
        if (slots.Entries.Count == 0)
        {
            var requested = int.TryParse(settingIni.Get("Slots", "Count", "32"), out var n) ? n : 32;
            var count = Math.Clamp(requested, 1, SlotMax);
            var prefix = settingIni.Get("Slots", "SerialPrefix", "DEMO");
            var list = new System.Collections.Generic.List<SlotEntry>(count);
            for (int i = 1; i <= count; i++)
                list.Add(new SlotEntry($"Slot{i}", $"{prefix}_{i:D3}", "1", "1", i.ToString(), "1", "1", i.ToString(), "1", "-", (200 + i).ToString(), "-"));
            slots = new SlotTable(list);
        }
        else if (slots.Entries.Count > SlotMax)
        {
            AppLog.Warn("App", $"Slot count {slots.Entries.Count} exceeds cap {SlotMax}; truncating.");
            slots = new SlotTable(slots.Entries.Take(SlotMax).ToList());
        }

        var session = new TestSession(station, commands, slots, plan, settingIni);
        var mainVm = new MainViewModel(session);
        var win = new MainWindow { DataContext = mainVm };
        win.Show();
    }
}

