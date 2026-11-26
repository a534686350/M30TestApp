using System;
using System.IO;
using System.Linq;
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
        var lastPlanFolder = AppPreferences.LastPlanFolder(settingIni);
        // Search the saved folder first, then fall back to root and all sub-folders.
        string? lastPlanPath = null;
        if (!string.IsNullOrWhiteSpace(lastPlanName) && Directory.Exists(AppPaths.TestConfigDir))
        {
            if (!string.IsNullOrWhiteSpace(lastPlanFolder))
            {
                var folderCandidate = Path.Combine(AppPaths.TestConfigDir, lastPlanFolder, lastPlanName + ".ini");
                if (File.Exists(folderCandidate))
                    lastPlanPath = folderCandidate;
            }

            var candidate = Path.Combine(AppPaths.TestConfigDir, lastPlanName + ".ini");
            if (lastPlanPath is null && File.Exists(candidate))
                lastPlanPath = candidate;

            if (lastPlanPath is null)
            {
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

        // ── 启动续测检测 ────────────────────────────────────────────────────
        // 程序一打开就找上次没测完的断点，问「是否继续」并让用户填重新保温多久。
        // 延后 1.2 秒：主窗口先画出来，否则模态框会抢在界面渲染之前弹，看着像卡死。
        // 纯界面截图的两个参数跳过（否则截图会被对话框挡住）；--shot-resume 专门验弹窗，不跳过。
        // 其余 --shot-* 一律跳过（含 --shot-config-section=xx 这类通用参数）。
        var isPlainShot = (e.Args.Contains("--shot-config-system") || e.Args.Contains("--shot-run-setup"))
                          || (e.Args.Any(a => a.StartsWith("--shot-", StringComparison.OrdinalIgnoreCase))
                              && !e.Args.Contains("--shot-resume"));
        if (!isPlainShot)
        {
            var resumeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            resumeTimer.Tick += (_, _) =>
            {
                resumeTimer.Stop();
                mainVm.PromptPendingResumeOnStartup();
            };
            resumeTimer.Start();
        }

        // ── 开发用自检钩子：带参数启动时，自动切到目标界面 → 渲染成 PNG 存到程序目录 → 退出。
        //    给「改完界面自己看一眼」用；不带参数正常运行完全不触发，对现场无影响。
        //      --shot-config-system  配置中心 / 系统设置（上报设备 ID）
        //      --shot-run-setup      选择运行方案窗口 / 工位页签（勾选格拖动选择）
        //      --shot-resume         启动续测弹窗（需先放好 data/checkpoint.json）
        //      --shot-finish         测试完成弹窗
        //      --shot-matrix         测试页多列矩阵（验横向滚动条）
        //      --shot-slots          顶部「工位」页（验表格）
        if (e.Args.Contains("--shot-finish"))
        {
            var run = mainVm.TestRun;
            for (var i = 1; i <= 16; i++)
                run.Rows.Add(new M30TestApp.Wpf.ViewModels.MatrixRowVm($"Slot{i}", $"260826_01#{i}"));
            run.OkCount = 14;
            run.WarnCount = 1;
            run.ErrCount = 1;

            var finishTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            finishTimer.Tick += (_, _) =>
            {
                finishTimer.Stop();
                run.ShowRunFinishedDialog(DateTime.Now.AddSeconds(-40), DateTime.Now);
            };
            finishTimer.Start();

            var finishShotTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            finishShotTimer.Tick += (_, _) =>
            {
                finishShotTimer.Stop();
                var dlg = Windows.OfType<Window>().FirstOrDefault(w => w.Title == "测试完成");
                if (dlg is not null)
                {
                    SaveWindowPng(dlg, "shot-finish-dialog.png");
                    dlg.Close();
                }
                else
                {
                    AppLog.Warn("Shot", "没等到完成弹窗");
                }
                Shutdown();
            };
            finishShotTimer.Start();
        }

        if (e.Args.Contains("--shot-resume"))
        {
            var dlgTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(6),
            };
            dlgTimer.Tick += (_, _) =>
            {
                dlgTimer.Stop();
                var dlg = Windows.OfType<Window>().FirstOrDefault(w => w.Title == "未完成测试");
                if (dlg is not null)
                {
                    SaveWindowPng(dlg, "shot-resume-dialog.png");
                    dlg.Close();
                }
                else
                {
                    AppLog.Warn("Shot", "没等到续测弹窗（断点文件可能不匹配当前方案）");
                }
                Shutdown();
            };
            dlgTimer.Start();
        }

        if (e.Args.Contains("--shot-longterm"))
        {
            // 自动跑一次长期稳定性测试（SIM），验证无设备时整条链路通不通：
            // 开 SIM → 把保温压到 0 → 开始测试 → 自动确认方案窗口。
            var warm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            warm.Tick += (_, _) =>
            {
                warm.Stop();
                mainVm.Settings.DebugMode = true;                 // 开 SIM 并重建设备
                foreach (var tp in mainVm.Session.Plan.TempPoints) tp.SoakMinutes = 0;   // 不等保温
                mainVm.TestMode = "LongTerm";
                mainVm.CurrentView = mainVm.TestModeView;
                mainVm.SelectedNavKey = "Test";
            };
            warm.Start();

            var startTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            startTimer.Tick += (_, _) =>
            {
                startTimer.Stop();
                if (mainVm.StartSelectedRunCommand.CanExecute(null))
                    mainVm.StartSelectedRunCommand.Execute(null);
            };
            startTimer.Start();

            // 方案窗口弹出来后自动点「确定」
            var confirmTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            confirmTimer.Tick += (_, _) =>
            {
                var dlg = Windows.OfType<M30TestApp.Wpf.Views.RunSetupWindow>().FirstOrDefault();
                if (dlg is null || dlg.DataContext is not M30TestApp.Wpf.ViewModels.RunSetupViewModel vm)
                    return;
                confirmTimer.Stop();
                vm.ApplyCommand.Execute(null);
            };
            confirmTimer.Start();

            var stopTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(35),
            };
            stopTimer.Tick += (_, _) =>
            {
                stopTimer.Stop();
                confirmTimer.Stop();
                SaveWindowPng(win, "shot-longterm.png");
                Shutdown();
            };
            stopTimer.Start();
        }

        if (e.Args.Contains("--shot-config-plan"))
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                mainVm.Config.SelectedSection = "方案";
                mainVm.CurrentView = mainVm.Config;
                mainVm.SelectedNavKey = "Settings";
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(win, "shot-config-plan.png");
        }

        if (e.Args.Contains("--shot-station"))
        {
            // 自动输管理员密码解锁上报区（自检专用，不写任何凭据）
            mainVm.Settings.TryUnlockStation(M30TestApp.Wpf.ViewModels.SettingsViewModel.DefaultAdminPassword);
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                mainVm.Config.SelectedSection = "系统设置";
                mainVm.CurrentView = mainVm.Config;
                mainVm.SelectedNavKey = "Settings";
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(win, "shot-station.png");
        }

        if (e.Args.Contains("--shot-config-system"))
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                mainVm.Config.SelectedSection = "系统设置";
                mainVm.CurrentView = mainVm.Config;
                mainVm.SelectedNavKey = "Settings";
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            ShotAndExit(win, "shot-config-system.png");
        }

        // 通用：--shot-config-section=<节名>（节名就是配置页左侧的页签名，例如 接口设置 / 温度采集 / 设备 / 工位 / 系统设置）
        // 每个配置页签都能单独截图自检，不用再一页一页加专门的参数。
        string? sectionName = null;
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--shot-config-section=", StringComparison.OrdinalIgnoreCase))
            {
                sectionName = a["--shot-config-section=".Length..].Trim();
                break;
            }
        }
        if (!string.IsNullOrEmpty(sectionName))
        {
            var target = sectionName;
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                mainVm.Config.SelectedSection = target;
                mainVm.CurrentView = mainVm.Config;
                mainVm.SelectedNavKey = "Settings";
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(win, $"shot-config-{target}.png");
        }

        if (e.Args.Contains("--shot-matrix"))
        {
            // 万用表自动测试的现场报表格式：R(Ω) | Usig0 | Usig50 | Usig100 | Usig50 | Usig0 | Temp
            var run = mainVm.TestRun;
            for (var i = 1; i <= 16; i++)
                run.Rows.Add(new M30TestApp.Wpf.ViewModels.MatrixRowVm($"Slot{i}", $"260826_01#{i}"));

            var points = session.Plan.PressurePoints;
            var keys = new[] { "T1_DMM_R", "T1P1_USG", "T1P2_USG", "T1P3_USG", "T1P2_USG_R", "T1P1_USG_R", "T1_OvenTemp" };
            foreach (var key in keys)
                run.MatrixColumns.Add(new M30TestApp.Wpf.ViewModels.MatrixColumnVm(
                    key, M30TestApp.Wpf.ViewModels.TestRunViewModel.FriendlyColumnHeader(key, points)));

            // 造几种状态验证配色：正常 / 警告 / 异常（红）/ 超量程
            var demoStatuses = new (int Row, int Col, M30TestApp.Core.Data.CellStatus St)[]
            {
                (1, 1, M30TestApp.Core.Data.CellStatus.Ok),
                (2, 2, M30TestApp.Core.Data.CellStatus.Warn),
                (3, 3, M30TestApp.Core.Data.CellStatus.Error),
                (4, 4, M30TestApp.Core.Data.CellStatus.Error),
                (5, 0, M30TestApp.Core.Data.CellStatus.Error),
                (6, 6, M30TestApp.Core.Data.CellStatus.Ok),
            };
            foreach (var (rowIdx, colIdx, st) in demoStatuses)
            {
                if (rowIdx >= run.Rows.Count) continue;
                var row = run.Rows[rowIdx];
                var key = keys[colIdx];
                row.Cells[key] = new M30TestApp.Core.Data.Cell
                {
                    Key = key,
                    Value = st == M30TestApp.Core.Data.CellStatus.Error ? "OL" : (1000 + rowIdx * 7).ToString("G6"),
                    RawValue = st == M30TestApp.Core.Data.CellStatus.Error ? double.NaN : 1000 + rowIdx * 7,
                    Status = st,
                };
            }
            foreach (var row in run.Rows) row.Cells.NotifyChanged();

            win.Dispatcher.BeginInvoke(new Action(() => mainVm.ShowTestCenterCommand.Execute(null)),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // 任务已改成独立窗口，截任务窗
            var matrixTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            matrixTimer.Tick += (_, _) =>
            {
                matrixTimer.Stop();
                var task = Application.Current.Windows.OfType<Views.TaskWindow>().FirstOrDefault();
                if (task is not null)
                    CaptureWindow(task, Path.Combine(AppPaths.BaseDir, "shot-matrix.png"));
                Application.Current.Shutdown();
            };
            matrixTimer.Start();
        }

        if (e.Args.Contains("--shot-slots"))
        {
            win.Dispatcher.BeginInvoke(new Action(() => mainVm.ShowSlotsCommand.Execute(null)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(win, "shot-slots.png");
        }

        // 偏好设置页（语言 / 更新 / 设备），从系统菜单进入，平时没有专属截图参数
        if (e.Args.Contains("--shot-settings"))
        {
            win.Dispatcher.BeginInvoke(new Action(() => mainVm.ShowSettingsCommand.Execute(null)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(win, "shot-settings.png");
        }

        // 主题化消息框自检：ShowDialog 会阻塞，所以先挂定时器（ShowDialog 内部有嵌套消息循环，定时器照常跑）
        if (e.Args.Contains("--shot-dialog"))
        {
            var dlgTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.6),
            };
            dlgTimer.Tick += (_, _) =>
            {
                dlgTimer.Stop();
                var dlg = Windows.OfType<Window>().FirstOrDefault(w => w is Views.AppDialog);
                if (dlg is not null)
                {
                    SaveWindowPng(dlg, "shot-dialog.png");
                    dlg.Close();
                }
                else
                {
                    AppLog.Warn("Shot", "没等到 AppDialog");
                }
                Shutdown();
            };
            dlgTimer.Start();

            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                Views.AppDialog.Show(
                    "保存失败：无法写入 Setting.ini（文件被其它程序占用）。\n请关闭占用该文件的程序后重试。",
                    "保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (e.Args.Contains("--shot-run-setup") || e.Args.Contains("--shot-run-setup-plan"))
        {
            var planTab = e.Args.Contains("--shot-run-setup-plan");
            var setupVm = new M30TestApp.Wpf.ViewModels.RunSetupViewModel(session, false);
            var setupWin = new M30TestApp.Wpf.Views.RunSetupWindow(setupVm)
            {
                Owner = win,
            };
            setupWin.Show();
            // 工位页签已移除，窗口只剩「选择方案」一页
            setupWin.Dispatcher.BeginInvoke(new Action(() => setupWin.SetupTabs.SelectedIndex = 0),
                System.Windows.Threading.DispatcherPriority.Loaded);
            ShotAndExit(setupWin, planTab ? "shot-run-setup-plan.png" : "shot-run-setup.png");
        }

        // 通用页面截图钩子：--shot-view=<key>
        // 快速测试 / 手动调试 / 日志这几页一直没有专属的 --shot-*，改版后没法自检，
        // 这里补一个按页面 key 切过去再截的通用入口。
        //   quick / manual / log / slots / auto / longterm / plan / config
        string? viewKey = null;
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--shot-view=", StringComparison.OrdinalIgnoreCase))
            {
                viewKey = a["--shot-view=".Length..].Trim().ToLowerInvariant();
                break;
            }
        }
        if (!string.IsNullOrEmpty(viewKey))
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (viewKey)
                {
                    case "quick":    mainVm.ShowQuickTestCommand.Execute(null); break;
                    case "manual":   mainVm.ShowManualCommand.Execute(null); break;
                    case "log":      mainVm.ShowLogCommand.Execute(null); break;
                    case "slots":    mainVm.ShowSlotsCommand.Execute(null); break;
                    case "auto":     mainVm.ShowTestRunCommand.Execute(null); break;
                    case "longterm": mainVm.ShowLongTermStabilityCommand.Execute(null); break;
                    case "plan":     mainVm.ShowConfigPlanCommand.Execute(null); break;
                    case "config":   mainVm.ShowConfigCommand.Execute(null); break;
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // 任务类页面现在开独立窗口，截图要指向任务窗而非主窗
            if (viewKey is "quick" or "manual" or "auto" or "longterm")
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var task = Application.Current.Windows.OfType<Views.TaskWindow>().FirstOrDefault();
                    if (task is not null)
                        CaptureWindow(task, Path.Combine(AppPaths.BaseDir, $"shot-view-{viewKey}.png"));
                    Application.Current.Shutdown();
                };
                timer.Start();
            }
            else
                ShotAndExit(win, $"shot-view-{viewKey}.png");
        }

        // 电子表格回写自检：--test-sheet-edit（改一格 → 看 C# 是否收到并落盘）
        if (e.Args.Contains("--test-sheet-edit"))
        {
            win.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var sheetView = new Views.SlotSheetView();
                    var probe = new Window
                    {
                        Title = "回写自检", Width = 1000, Height = 560,
                        Content = sheetView,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    };
                    probe.Show();

                    var received = 0;
                    string lastSerial = "";
                    sheetView.RowsChanged += (_, rows) =>
                    {
                        received++;
                        lastSerial = rows.Count > 0 ? rows[0].serialNo : "";
                    };

                    await Task.Delay(1500);
                    sheetView.LoadSlots(session.Slots.Entries);
                    await Task.Delay(1500);

                    var before = session.Slots.Entries[0].SerialNo;
                    await sheetView.SetCellForTestAsync(0, 1, "TEST123");
                    await Task.Delay(1200);

                    Console.WriteLine($"SHEET-EDIT: before={before} received={received} lastSerial={lastSerial} ready={sheetView.IsReady}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("SHEET-EDIT ERROR: " + ex.Message);
                }
                finally
                {
                    Application.Current.Shutdown();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 电子表格自检：--shot-sheet（WebView2 内容普通截图截不到，用它自己的 CapturePreview）
        if (e.Args.Contains("--shot-sheet"))
        {
            win.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // 独立开一个窗口放表格控件，直接验证控件本身（不依赖配置页 Tab 懒加载）
                    var sheetView = new Views.SlotSheetView();
                    var probe = new Window
                    {
                        Title = "电子表格自检",
                        Width = 1100,
                        Height = 620,
                        Content = sheetView,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
                    };
                    probe.Show();

                    await Task.Delay(1500);
                    sheetView.LoadSlots(session.Slots.Entries);
                    await Task.Delay(2500);

                    var ok = await sheetView.CaptureAsync(Path.Combine(AppPaths.BaseDir, "shot-sheet.png"));
                    Console.WriteLine($"SHOT-SHEET: {(ok ? "OK" : "FAIL")} ready={sheetView.IsReady}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("SHOT-SHEET ERROR: " + ex.Message);
                }
                finally
                {
                    Application.Current.Shutdown();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 设备预检窗口自检：--shot-preflight
        if (e.Args.Contains("--shot-preflight"))
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                var w = new Views.PreflightWindow(session, session.Context.Settings) { Owner = win };
                w.Show();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            var pfTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            pfTimer.Tick += (_, _) =>
            {
                pfTimer.Stop();
                var target = Application.Current.Windows.OfType<Views.PreflightWindow>().FirstOrDefault();
                if (target is not null)
                    CaptureWindow(target, Path.Combine(AppPaths.BaseDir, "shot-preflight.png"));
                Application.Current.Shutdown();
            };
            pfTimer.Start();
        }

        // 工位数量自动落盘自检：--regen-slots=54
        // 设数量 → SlotCount setter → RegenerateSlots → 自动保存 CSV/ini/session。
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--regen-slots=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(a["--regen-slots=".Length..], out var slotCount))
            {
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    mainVm.Config.SlotCount = slotCount;
                    AppLog.Info("SelfTest",
                        $"regen-slots={slotCount}：UI={mainVm.Config.Slots.Count}，"
                        + $"session={session.Slots.Entries.Count}");
                }), System.Windows.Threading.DispatcherPriority.Loaded);
                ShotAndExit(win, $"shot-regen-slots-{slotCount}.png");
            }
        }

        // 独立面板窗口自检：--shot-panel=batch / freq
        string? panelKey = null;
        foreach (var a in e.Args)
        {
            if (a.StartsWith("--shot-panel=", StringComparison.OrdinalIgnoreCase))
            {
                panelKey = a["--shot-panel=".Length..].Trim().ToLowerInvariant();
                break;
            }
        }
        if (!string.IsNullOrEmpty(panelKey))
        {
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (panelKey == "batch")
                    mainVm.Manual.OpenBatchPanelCommand.Execute(null);
                else if (panelKey == "freq")
                    mainVm.Manual.OpenFrequencyPanelCommand.Execute(null);
                else if (panelKey == "dmm")
                    mainVm.Manual.OpenDmmPanelCommand.Execute(null);
                else if (panelKey == "runslots")
                    new Views.RunSlotsPreviewWindow(session.Slots.Entries).Show();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // PanelWindow.Show() 是非模态，ShotAndExit 需要指向新窗口：
            // 7 秒后从 Application.Windows 里按标题取出来截。
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                System.Windows.Window? target = Application.Current.Windows
                    .OfType<Views.PanelWindow>().FirstOrDefault();
                target ??= Application.Current.Windows
                    .OfType<Views.RunSlotsPreviewWindow>().FirstOrDefault();
                if (target is not null)
                {
                    var name = Path.Combine(AppPaths.BaseDir, $"shot-panel-{panelKey}.png");
                    CaptureWindow(target, name);
                }
                Application.Current.Shutdown();
            };
            timer.Start();
        }
    }

    private static void CaptureWindow(System.Windows.Window win, string fileName)
    {
        var rect = new System.Windows.Rect(0, 0, win.ActualWidth, win.ActualHeight);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)rect.Width, (int)rect.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(win);
        using var fs = File.Create(fileName);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        enc.Save(fs);
    }

    /// <summary>开发用：把窗口渲染成 PNG 后退出（配合上面的 --shot-* 参数）。</summary>
    private static void ShotAndExit(System.Windows.Window win, string fileName)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(7),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 先把窗口拉到足够大，避免页签内容超出窗口被截掉（截图只是自检用）
            if (win.WindowState == System.Windows.WindowState.Maximized)
                win.WindowState = System.Windows.WindowState.Normal;
            win.Width = Math.Max(win.ActualWidth, 1500);
            win.Height = Math.Max(win.ActualHeight, 1240);
            win.UpdateLayout();
            SaveWindowPng(win, fileName);
            win.Close();
            System.Windows.Application.Current?.Shutdown();
        };
        timer.Start();
    }

    /// <summary>把窗口渲染成 PNG 存到程序目录（开发自检用，失败只记日志不抛）。</summary>
    private static void SaveWindowPng(System.Windows.Window win, string fileName)
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(win.ActualWidth * dpi.DpiScaleX),
                (int)(win.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(win);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));

            var path = System.IO.Path.Combine(M30TestApp.Core.Common.AppPaths.BaseDir, fileName);
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            M30TestApp.Core.Common.AppLog.Info("Shot", "saved " + path);
        }
        catch (Exception ex)
        {
            M30TestApp.Core.Common.AppLog.Error("Shot", ex.Message);
        }
    }
}
