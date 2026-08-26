using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core.Common;

namespace M30TestApp.Wpf.Services;

public static class SelfUpdater
{
    public static string WorkDir => Path.Combine(Path.GetTempPath(), "M30TestApp_update");

    /// <summary>应用目录下的版本备份文件夹（升级前自动生成，供「回退到上一版本」使用）。</summary>
    public static string RollbackDir => Path.Combine(AppPaths.BaseDir, "rollback");
    public static string RollbackZipPath => Path.Combine(RollbackDir, "previous.zip");
    public static string RollbackVersionFile => Path.Combine(RollbackDir, "version.txt");

    public static bool HasRollbackBackup => File.Exists(RollbackZipPath);

    /// <summary>读取备份的上一版本号（如 "1.2.36"）；无备份时返回空串。</summary>
    public static string ReadRollbackVersion()
    {
        try
        {
            return File.Exists(RollbackVersionFile)
                ? File.ReadAllText(RollbackVersionFile).Trim()
                : "";
        }
        catch { return ""; }
    }

    private static readonly HttpClient _http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "M30TestApp" } }
    };

    public static async Task<string> DownloadAsync(
        string assetUrl,
        string assetName,
        IProgress<int>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(WorkDir);
        var zipPath = Path.Combine(WorkDir, assetName);

        try
        {
            using var resp = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(zipPath))
            {
                var buf = new byte[81920];
                long read = 0;
                var last = -1;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0 && progress is not null)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct != last)
                        {
                            progress.Report(pct);
                            last = pct;
                        }
                    }
                }
            }

            Validate(zipPath);
            return zipPath;
        }
        catch
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
                // Preserve the original download exception.
            }

            throw;
        }
    }

    private static void Validate(string zipPath)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists || info.Length == 0)
            throw new InvalidDataException("Downloaded update package is empty.");

        using var z = ZipFile.OpenRead(zipPath);
        if (z.Entries.Count == 0)
            throw new InvalidDataException("Downloaded update package has no files.");
    }

    public static void LaunchUpdaterAndExit(string zipPath, string targetDir)
    {
        var cmdPath = Path.Combine(WorkDir, "updater.cmd");
        File.WriteAllText(cmdPath, BuildUpdaterScript(), Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{cmdPath}\" \"{zipPath}\" \"{targetDir}\"\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        AppLog.Info("Updater", $"Launched updater for {zipPath} -> {targetDir}");
        Application.Current.Shutdown();
    }

    /// <summary>回退到上一版本：写 rollback.cmd → 退出应用，由脚本完成换装并重启。</summary>
    public static void LaunchRollbackAndExit(string targetDir)
    {
        if (!HasRollbackBackup)
            throw new InvalidOperationException("No rollback backup found.");

        var cmdPath = Path.Combine(WorkDir, "rollback.cmd");
        File.WriteAllText(cmdPath, BuildRollbackScript(), Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{cmdPath}\" \"{targetDir}\"\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        AppLog.Info("Updater", $"Launched rollback from {RollbackZipPath} -> {targetDir}");
        Application.Current.Shutdown();
    }

    private static string BuildUpdaterScript()
    {
        const string slotCsv = "\u5DE5\u4F4D\u5BF9\u5E94\u8868.csv";
        return string.Join("\r\n", new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            "timeout /t 3 /nobreak >nul",
            "set \"ZIP=%~1\"",
            "set \"TARGET=%~2\"",
            "set \"BACKUP=%~dp0local-setting-backup\"",
            "set \"ROLLBACK=%TARGET%\\rollback\"",
            "if exist \"%BACKUP%\" rd /s /q \"%BACKUP%\"",
            "mkdir \"%BACKUP%\\setting\" >nul 2>nul",
            "if exist \"%TARGET%\\setting\\Setting.ini\" copy /Y \"%TARGET%\\setting\\Setting.ini\" \"%BACKUP%\\setting\\Setting.ini\" >nul",
            "if exist \"%TARGET%\\setting\\Config.ini\" copy /Y \"%TARGET%\\setting\\Config.ini\" \"%BACKUP%\\setting\\Config.ini\" >nul",
            $"if exist \"%TARGET%\\setting\\{slotCsv}\" copy /Y \"%TARGET%\\setting\\{slotCsv}\" \"%BACKUP%\\setting\\{slotCsv}\" >nul",
            "if exist \"%TARGET%\\setting\\TestConfig\" xcopy /E /I /Y \"%TARGET%\\setting\\TestConfig\" \"%BACKUP%\\setting\\TestConfig\" >nul",

            // ── 升级前备份当前版本主程序，供「回退到上一版本」使用 ──
            "powershell -NoProfile -Command \"$ErrorActionPreference='Stop'; try { New-Item -ItemType Directory -Force -Path '%ROLLBACK%' | Out-Null; $files = Get-ChildItem -Path '%TARGET%\\*' -File -Include '*.exe','*.dll'; $exe = Join-Path '%TARGET%' 'M30TestApp.V2.exe'; if (-not (Test-Path $exe)) { throw 'main exe missing' }; if ($files) { Compress-Archive -Path $files.FullName -DestinationPath '%ROLLBACK%\\previous.zip' -Force }; (Get-Item $exe).VersionInfo.FileVersion | Out-File -Encoding utf8 '%ROLLBACK%\\version.txt' } catch { exit 1 }\"",
            "rem 备份失败不阻断升级（旧版无备份则隐藏回退入口）",

            "powershell -NoProfile -Command \"Expand-Archive -Path '%~1' -DestinationPath '%~2' -Force\"",
            "if errorlevel 1 (",
            "    echo Update failed: extraction error.",
            "    pause",
            "    exit /b 1",
            ")",
            "if not exist \"%TARGET%\\M30TestApp.V2.exe\" (",
            "    echo Update failed: main exe missing after extraction.",
            "    pause",
            "    exit /b 1",
            ")",
            "if not exist \"%TARGET%\\setting\" mkdir \"%TARGET%\\setting\" >nul 2>nul",
            "if exist \"%BACKUP%\\setting\\Setting.ini\" copy /Y \"%BACKUP%\\setting\\Setting.ini\" \"%TARGET%\\setting\\Setting.ini\" >nul",
            "if exist \"%BACKUP%\\setting\\Config.ini\" copy /Y \"%BACKUP%\\setting\\Config.ini\" \"%TARGET%\\setting\\Config.ini\" >nul",
            $"if exist \"%BACKUP%\\setting\\{slotCsv}\" copy /Y \"%BACKUP%\\setting\\{slotCsv}\" \"%TARGET%\\setting\\{slotCsv}\" >nul",
            "if exist \"%BACKUP%\\setting\\TestConfig\" xcopy /E /I /Y \"%BACKUP%\\setting\\TestConfig\" \"%TARGET%\\setting\\TestConfig\" >nul",
            "if exist \"%BACKUP%\" rd /s /q \"%BACKUP%\"",
            "start \"\" \"%TARGET%\\M30TestApp.V2.exe\"",
            "del \"%ZIP%\"",
            "(goto) 2>nul & del \"%~f0\"",
            ""
        });
    }

    private static string BuildRollbackScript()
    {
        return string.Join("\r\n", new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            "timeout /t 3 /nobreak >nul",
            "set \"TARGET=%~1\"",
            "set \"ROLLBACK=%TARGET%\\rollback\"",
            "if not exist \"%ROLLBACK%\\previous.zip\" (",
            "    echo Rollback failed: no backup.",
            "    pause",
            "    exit /b 1",
            ")",
            "powershell -NoProfile -Command \"Expand-Archive -Path '%ROLLBACK%\\previous.zip' -DestinationPath '%TARGET%' -Force\"",
            "if errorlevel 1 (",
            "    echo Rollback failed: extraction error.",
            "    pause",
            "    exit /b 1",
            ")",
            "if not exist \"%TARGET%\\M30TestApp.V2.exe\" (",
            "    echo Rollback failed: main exe missing.",
            "    pause",
            "    exit /b 1",
            ")",
            "rd /s /q \"%ROLLBACK%\"",
            "start \"\" \"%TARGET%\\M30TestApp.V2.exe\"",
            "(goto) 2>nul & del \"%~f0\"",
            ""
        });
    }
}
