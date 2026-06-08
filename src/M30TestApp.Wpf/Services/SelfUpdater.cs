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
            "if exist \"%BACKUP%\" rd /s /q \"%BACKUP%\"",
            "mkdir \"%BACKUP%\\setting\" >nul 2>nul",
            "if exist \"%TARGET%\\setting\\Setting.ini\" copy /Y \"%TARGET%\\setting\\Setting.ini\" \"%BACKUP%\\setting\\Setting.ini\" >nul",
            "if exist \"%TARGET%\\setting\\Config.ini\" copy /Y \"%TARGET%\\setting\\Config.ini\" \"%BACKUP%\\setting\\Config.ini\" >nul",
            $"if exist \"%TARGET%\\setting\\{slotCsv}\" copy /Y \"%TARGET%\\setting\\{slotCsv}\" \"%BACKUP%\\setting\\{slotCsv}\" >nul",
            "if exist \"%TARGET%\\setting\\TestConfig\" xcopy /E /I /Y \"%TARGET%\\setting\\TestConfig\" \"%BACKUP%\\setting\\TestConfig\" >nul",
            "powershell -NoProfile -Command \"Expand-Archive -Path '%~1' -DestinationPath '%~2' -Force\"",
            "if errorlevel 1 (",
            "    echo Update failed: extraction error.",
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
}
