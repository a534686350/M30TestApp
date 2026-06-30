using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;
using M30TestApp.Wpf.Services;
using M30TestApp.Wpf.Themes;
using M30TestApp.Wpf.Views;

namespace M30TestApp.Wpf.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    public const string RepoOwner = "a534686350";
    public const string RepoName = "M30TestApp";
    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

    private const string GiteeOwner = "hl515";
    private const string GiteeRepo = "m30-test-app";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "M30TestApp" } }
    };

    public string AppVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private string _language = "zh-CN";
    public string Language
    {
        get => _language;
        set
        {
            if (SetField(ref _language, value))
                ApplyLanguage(value);
        }
    }

    private string _theme = "Light";
    public string Theme
    {
        get => _theme;
        set
        {
            if (SetField(ref _theme, value))
                ApplyTheme(value);
        }
    }

    private bool _debugMode;
    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetField(ref _debugMode, value))
                ApplyDebugMode(value);
        }
    }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; set => SetField(ref _updateStatus, value); }

    private int _updateProgress;
    public int UpdateProgress
    {
        get => _updateProgress;
        set
        {
            if (SetField(ref _updateProgress, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(ShowUpdateProgress));
                OnPropertyChanged(nameof(IsUpdateIndeterminate));
            }
        }
    }

    public bool ShowUpdateProgress => IsCheckingUpdate || UpdateProgress > 0;
    public bool IsUpdateIndeterminate => IsCheckingUpdate && UpdateProgress <= 0;

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        set
        {
            if (SetField(ref _isCheckingUpdate, value))
            {
                OnPropertyChanged(nameof(ShowUpdateProgress));
                OnPropertyChanged(nameof(IsUpdateIndeterminate));
            }
        }
    }

    public RelayCommand OpenRepoCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }

    private readonly TestSession _session;

    public SettingsViewModel(TestSession session)
    {
        _session = session;
        _language = LanguageHelper.Normalize(AppPreferences.Language(session.Context.Settings));
        _theme = ThemeHelper.Normalize(AppPreferences.Theme(session.Context.Settings));
        _debugMode = AppPreferences.DebugMode(session.Context.Settings);

        OpenRepoCommand = new RelayCommand(_ =>
        {
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Settings", $"无法打开浏览器: {ex.Message}"); }
        });

        CheckUpdateCommand = new AsyncRelayCommand(CheckForUpdateAsync);
    }

    public Task CheckForUpdateOnStartupAsync() => CheckForUpdateAsync();

    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate) return;

        UpdateProgress = 0;
        IsCheckingUpdate = true;
        UpdateStatus = Language == "zh-CN" ? "\u6b63\u5728\u68c0\u67e5\u66f4\u65b0..." : "Checking for updates...";
        var currentVersion = AppVersion;
        UpdateProgressWindow? progressWindow = null;
        Window? owner = null;
        var ownerWasEnabled = true;
        var willRestart = false;

        try
        {
            var release = await FetchLatestReleaseWithFallbackAsync();
            var latest = release.Tag.TrimStart('v', 'V');
            if (!Version.TryParse(latest, out var latestVer) ||
                !Version.TryParse(currentVersion, out var currentVer))
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"\u7248\u672c\u53f7\u89e3\u6790\u5931\u8d25\uff08latest={latest}, current={currentVersion}\uff09"
                    : $"Version parse error (latest={latest}, current={currentVersion})";
                return;
            }

            if (latestVer <= currentVer)
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"\u5df2\u662f\u6700\u65b0\u7248\u672c v{currentVersion}"
                    : $"Up to date (v{currentVersion})";
                return;
            }

            var message = Language == "zh-CN"
                ? $"\u53d1\u73b0\u65b0\u7248\u672c v{latest}\uff08\u5f53\u524d v{currentVersion}\uff09\n\n\u662f\u5426\u7acb\u5373\u4e0b\u8f7d\u5e76\u5b89\u88c5\uff1f\n\u9009\u62e9\u201c\u5426\u201d\u53ef\u7ee7\u7eed\u4f7f\u7528\u5f53\u524d\u7248\u672c\u3002"
                : $"New version v{latest} is available (current v{currentVersion}).\n\nDownload and install now?\nChoose No to keep using the current version.";
            if (MessageBox.Show(Application.Current.MainWindow, message, "M30TestApp", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"\u53d1\u73b0\u65b0\u7248\u672c v{latest}\uff0c\u5df2\u8df3\u8fc7\uff08\u53ef\u5728\u8bbe\u7f6e\u9875\u624b\u52a8\u68c0\u67e5\u66f4\u65b0\uff09"
                    : $"New version v{latest} available, skipped (check manually in Settings).";
                return;
            }

            owner = Application.Current.MainWindow;
            if (owner is not null)
            {
                ownerWasEnabled = owner.IsEnabled;
                owner.IsEnabled = false;
            }

            progressWindow = new UpdateProgressWindow
            {
                Owner = owner,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
            };
            progressWindow.SetIndeterminate(Language == "zh-CN" ? $"\u6b63\u5728\u51c6\u5907\u4e0b\u8f7d v{latest}..." : $"Preparing to download v{latest}...");
            progressWindow.Show();
            progressWindow.Activate();

            UpdateStatus = Language == "zh-CN"
                ? $"\u53d1\u73b0\u65b0\u7248\u672c v{latest}\uff0c\u6b63\u5728\u4e0b\u8f7d..."
                : $"New version v{latest} found. Downloading...";
            var progress = new Progress<int>(p =>
            {
                UpdateProgress = p;
                UpdateStatus = (Language == "zh-CN" ? "\u6b63\u5728\u4e0b\u8f7d " : "Downloading ") + p + "%";
                progressWindow.SetStatus(UpdateStatus);
                progressWindow.SetProgress(p);
            });

            var zipPath = await SelfUpdater.DownloadAsync(release.AssetUrl, release.AssetName, progress);

            UpdateProgress = 100;
            UpdateStatus = Language == "zh-CN"
                ? "\u4e0b\u8f7d\u5b8c\u6210\uff0c\u6b63\u5728\u51c6\u5907\u91cd\u542f\u5e94\u7528..."
                : "Download complete, restarting...";
            progressWindow.SetProgress(100);
            progressWindow.SetStatus(UpdateStatus);
            await Task.Delay(500);

            willRestart = true;
            progressWindow.AllowClose();
            SelfUpdater.LaunchUpdaterAndExit(zipPath, AppPaths.BaseDir);
        }
        catch (Exception ex)
        {
            UpdateStatus = (Language == "zh-CN" ? "\u68c0\u67e5\u66f4\u65b0\u5931\u8d25\uff1a" : "Update check failed: ") + ex.Message;
            progressWindow?.SetStatus(UpdateStatus);
            progressWindow?.SetProgress(0);
            progressWindow?.AllowClose();
            progressWindow?.Close();
            AppLog.Warn("Update", UpdateStatus);
        }
        finally
        {
            if (!willRestart && owner is not null)
                owner.IsEnabled = ownerWasEnabled;
            IsCheckingUpdate = false;
        }
    }

    private static async Task<(string Tag, string AssetUrl, string AssetName)> FetchLatestReleaseWithFallbackAsync()
    {
        var candidates = new List<(string Host, string Tag, string AssetUrl, string AssetName)>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var release = await TryFetchLatestReleaseAsync("gitee", cts.Token);
            candidates.Add(("gitee", release.Tag, release.AssetUrl, release.AssetName));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update", $"Gitee update check failed: {ex.Message}");
        }

        try
        {
            var release = await TryFetchLatestReleaseAsync("github", CancellationToken.None);
            candidates.Add(("github", release.Tag, release.AssetUrl, release.AssetName));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update", $"GitHub update check failed: {ex.Message}");
        }

        var best = candidates
            .Select(c => new
            {
                c.Host,
                c.Tag,
                c.AssetUrl,
                c.AssetName,
                Version = Version.TryParse(c.Tag.TrimStart('v', 'V'), out var v) ? v : new Version(0, 0)
            })
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

        if (best is not null)
        {
            AppLog.Info("Update", $"Latest release selected from {best.Host}: {best.Tag}");
            return (best.Tag, best.AssetUrl, best.AssetName);
        }

        throw new InvalidOperationException("No release found from Gitee or GitHub");
    }

    private static async Task<(string Tag, string AssetUrl, string AssetName)> TryFetchLatestReleaseAsync(
        string host, CancellationToken ct)
    {
        string url = host == "github"
            ? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest"
            : $"https://gitee.com/api/v5/repos/{GiteeOwner}/{GiteeRepo}/releases/latest";

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.GetProperty("assets");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var browserUrl)
                ? browserUrl.GetString() ?? ""
                : asset.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";

            if (!string.IsNullOrWhiteSpace(downloadUrl))
                return (tag, downloadUrl, name);
        }

        throw new InvalidOperationException("No .zip asset found in the latest release");
    }

    private void ApplyLanguage(string lang)
    {
        lang = LanguageHelper.Normalize(lang);
        AppPreferences.Set(_session.Context.Settings, "Language", lang);
        try { _session.Context.Settings.Save(AppPaths.SettingIni); } catch { }
        LanguageHelper.Apply(lang);
    }

    private void ApplyTheme(string theme)
    {
        theme = ThemeHelper.Normalize(theme);
        AppPreferences.Set(_session.Context.Settings, "Theme", theme);
        try { _session.Context.Settings.Save(AppPaths.SettingIni); } catch { }
        ThemeHelper.Apply(theme);
    }

    private void ApplyDebugMode(bool enabled)
    {
        AppPreferences.SetBool(_session.Context.Settings, "DebugMode", enabled);
        try { _session.Context.Settings.Save(AppPaths.SettingIni); } catch { }

        try
        {
            _session.RebuildDevices(enabled);
            UpdateStatus = enabled
                ? "调试模式已开启：不连接真实硬件，使用模拟设备。"
                : "调试模式已关闭：后续连接按设备配置执行。";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"切换调试模式失败：{ex.Message}";
            AppLog.Error("Settings", UpdateStatus);
        }
    }
}
