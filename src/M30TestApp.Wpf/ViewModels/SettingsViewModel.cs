using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Common;
using M30TestApp.Wpf.Mvvm;
using M30TestApp.Wpf.Themes;

namespace M30TestApp.Wpf.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    public const string RepoOwner = "a534686350";
    public const string RepoName = "M30TestApp";
    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

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
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = tagName.TrimStart('v', 'V');
            var current = AppVersion;

            if (string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0)
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"发现新版本 v{latest}（当前 v{current}）"
                    : $"New version v{latest} available (current v{current})";

                var msg = Language == "zh-CN"
                    ? $"发现新版本 v{latest}，是否打开下载页面？"
                    : $"New version v{latest} found. Open download page?";

                if (MessageBox.Show(msg, "M30TestApp", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    var releaseUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";
                    Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                }
            }
            else
            {
                UpdateStatus = Language == "zh-CN"
                    ? $"已是最新版本 v{current}"
                    : $"Up to date (v{current})";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            UpdateStatus = Language == "zh-CN"
                ? "当前仓库暂无发布版本，请到 GitHub 查看"
                : "No published release yet. Please check GitHub.";
        }
        catch (Exception ex)
        {
            UpdateStatus = Language == "zh-CN"
                ? $"检查更新失败: {ex.Message}"
                : $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
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
}
