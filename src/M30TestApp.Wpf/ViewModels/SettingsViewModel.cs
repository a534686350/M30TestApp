using System;
using System.Diagnostics;
using System.Net;
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

    // ── Version ──
    public string AppVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // ── Language ──
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

    // ── Theme (reserved for future dark mode) ──
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

    // ── Update check ──
    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; set => SetField(ref _updateStatus, value); }

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate { get => _isCheckingUpdate; set => SetField(ref _isCheckingUpdate, value); }

    // ── Commands ──
    public RelayCommand OpenRepoCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }

    private readonly TestSession _session;

    public SettingsViewModel(TestSession session)
    {
        _session = session;

        // Load saved language preference
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

    private async Task CheckForUpdateAsync()
    {
        IsCheckingUpdate = true;
        UpdateStatus = Language == "zh-CN" ? "正在检查更新…" : "Checking for updates…";
        var currentVersion = AppVersion;
        try
        {
            // Try Gitee first (国内访问快), fall back to GitHub
            (string tag, string assetUrl, string assetName) release;
            try
            {
                using var giteeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                release = await TryFetchLatestReleaseAsync("gitee", giteeCts.Token);
            }
            catch (Exception)
            {
                UpdateStatus = Language == "zh-CN"
                    ? "Gitee 不可达，切换到 GitHub 镜像…"
                    : "Gitee unreachable, switching to GitHub mirror…";
                release = await TryFetchLatestReleaseAsync("github", CancellationToken.None);
            }

            var latest = release.tag.TrimStart('v', 'V');
            if (!Version.TryParse(latest, out var latestVer)
                || !Version.TryParse(currentVersion, out var currentVer))
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"版本号解析失败 (latest={latest}, current={currentVersion})"
                    : $"Version parse error (latest={latest}, current={currentVersion})";
                return;
            }

            if (latestVer <= currentVer)
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"已是最新版本 v{currentVersion}"
                    : $"Up to date (v{currentVersion})";
                return;
            }

            UpdateStatus = Language == "zh-CN"
                ? $"发现新版本 v{latest}（当前 v{currentVersion}）"
                : $"New version v{latest} available (current v{currentVersion})";

            var msg = Language == "zh-CN"
                ? $"发现新版本 v{latest}，是否下载并自动安装？"
                : $"New version v{latest} found. Download and install?";

            if (MessageBox.Show(msg, "M30TestApp", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            UpdateStatus = Language == "zh-CN" ? "正在下载…" : "Downloading…";
            var progress = new Progress<int>(p =>
                UpdateStatus = (Language == "zh-CN" ? "正在下载 " : "Downloading ") + p + "%");

            var zipPath = await SelfUpdater.DownloadAsync(release.assetUrl, release.assetName, progress);

            UpdateStatus = Language == "zh-CN" ? "下载完成，即将重启应用…" : "Download complete, restarting…";
            SelfUpdater.LaunchUpdaterAndExit(zipPath, AppPaths.BaseDir);
        }
        catch (Exception ex)
        {
            UpdateStatus = (Language == "zh-CN" ? "检查更新失败: " : "Update check failed: ") + ex.Message;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private static async Task<(string tag, string assetUrl, string assetName)> TryFetchLatestReleaseAsync(
        string host, CancellationToken ct)
    {
        string owner, repo, url;
        if (host == "github")
        {
            owner = RepoOwner; repo = RepoName;
            url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        }
        else
        {
            owner = GiteeOwner; repo = GiteeRepo;
            url = $"https://gitee.com/api/v5/repos/{owner}/{repo}/releases/latest";
        }

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                return (tag, downloadUrl, name);
            }
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
                ? "调试模式已开启：不连接真实硬件，指令记录发送并模拟接收。"
                : "调试模式已关闭：后续连接按设备配置执行。";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"切换调试模式失败：{ex.Message}";
            AppLog.Error("Settings", UpdateStatus);
        }
    }
}
