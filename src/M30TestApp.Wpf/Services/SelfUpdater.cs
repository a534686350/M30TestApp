using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
            int last = -1;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && progress is not null)
                {
                    var pct = (int)(read * 100 / total);
                    if (pct != last) { progress.Report(pct); last = pct; }
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
            throw new InvalidDataException("下载文件为空");
        using var z = ZipFile.OpenRead(zipPath);
        if (z.Entries.Count == 0)
            throw new InvalidDataException("压缩包内容为空");
    }

    public static void LaunchUpdaterAndExit(string zipPath, string targetDir)
    {
        var cmdPath = Path.Combine(WorkDir, "updater.cmd");
        File.WriteAllText(cmdPath, BuildUpdaterScript(), System.Text.Encoding.Default);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{cmdPath}\" \"{zipPath}\" \"{targetDir}\"\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        AppLog.Info("Updater", $"Launched updater for {zipPath} → {targetDir}");
        Application.Current.Shutdown();
    }

    private static string BuildUpdaterScript() => string.Join("\r\n", new[]
    {
        "@echo off",
        "chcp 65001 >nul",
        "timeout /t 3 /nobreak >nul",
        "if exist \"%~2\\setting\\Setting.ini\" copy /Y \"%~2\\setting\\Setting.ini\" \"%~dp0Setting.ini.bak\" >nul",
        "powershell -NoProfile -Command \"Expand-Archive -Path '%~1' -DestinationPath '%~2' -Force\"",
        "if errorlevel 1 (",
        "    echo Update failed: extraction error.",
        "    pause",
        "    exit /b 1",
        ")",
        "if exist \"%~dp0Setting.ini.bak\" (",
        "    copy /Y \"%~dp0Setting.ini.bak\" \"%~2\\setting\\Setting.ini\" >nul",
        "    del \"%~dp0Setting.ini.bak\"",
        ")",
        "start \"\" \"%~2\\M30TestApp.V2.exe\"",
        "del \"%~1\"",
        "(goto) 2>nul & del \"%~f0\"",
        ""
    });
}
