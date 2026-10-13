using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Net;

/// <summary>
/// 「站点标识」配置：每台设备一个唯一 ID，用来在总控制台里区分是哪一台。
/// 存在 <c>Setting.ini</c> 的 <c>[Station]</c> 段。
/// </summary>
public sealed class StationConfig
{
    public const string Section = "Station";

    /// <summary>站点唯一 ID（必填，例如 M30-A01）。空 = 没绑定，不上报。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（控制台上显示；留空则用 ID）。</summary>
    public string Name { get; set; } = "";

    /// <summary>总控制台地址，例如 http://54.206.27.158/ 。</summary>
    public string HubUrl { get; set; } = "";

    /// <summary>上报令牌，要和服务器 M30_HUB_TOKEN 一致。</summary>
    public string Token { get; set; } = "";

    /// <summary>是否启用上报。</summary>
    public bool Enabled { get; set; }

    /// <summary>上报间隔（秒），下限 5。</summary>
    public int IntervalSec { get; set; } = 5;

    public bool IsReady =>
        Enabled && !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(HubUrl);

    public static StationConfig Load(IniFile ini) => new()
    {
        Id = ini.Get(Section, "Id", ""),
        Name = ini.Get(Section, "Name", ""),
        HubUrl = ini.Get(Section, "HubUrl", ""),
        Token = ini.Get(Section, "Token", ""),
        Enabled = ini.Get(Section, "Enabled", "0") is "1" or "true" or "True",
        IntervalSec = int.TryParse(ini.Get(Section, "IntervalSec", "5"), out var n) ? Math.Max(5, n) : 5,
    };

    public void SaveTo(IniFile ini)
    {
        ini.Set(Section, "Id", Id?.Trim() ?? "");
        ini.Set(Section, "Name", Name?.Trim() ?? "");
        ini.Set(Section, "HubUrl", HubUrl?.Trim() ?? "");
        ini.Set(Section, "Token", Token?.Trim() ?? "");
        ini.Set(Section, "Enabled", Enabled ? "1" : "0");
        ini.Set(Section, "IntervalSec", Math.Max(5, IntervalSec).ToString());
    }
}

/// <summary>上报给总控制台的一份状态快照。</summary>
public sealed record StationSnapshot(
    string State, string Plan, string Step, double Progress, int Alarms,
    int SlotTotal, int SlotOk, int SlotWarn, int SlotErr, string? Note = null);

/// <summary>
/// 站点状态上报器（后台循环）。
///
/// 铁律：**离线优先** —— 连不上控制台只是本轮跳过 + 退避重试，绝不抛异常、
/// 绝不阻塞测试流程。控制台那边只是"看"，测试能不能跑跟它没关系。
/// </summary>
public sealed class StationReporter : IDisposable
{
    private readonly Func<StationSnapshot> _snapshot;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private CancellationTokenSource? _cts;
    private StationConfig _cfg = new();

    /// <summary>给人看的最近一次状态（设置页显示用）。</summary>
    public string StatusText { get; private set; } = "未启用";

    /// <summary>状态文字变化（可能来自后台线程，UI 侧需自行切回 UI 线程）。</summary>
    public event Action? StatusChanged;

    public StationReporter(Func<StationSnapshot> snapshot) => _snapshot = snapshot;

    public void Apply(StationConfig cfg)
    {
        _cfg = cfg;
        Stop();
        if (cfg.IsReady) Start();
        else
        {
            SetStatus(cfg.Enabled
                ? "未启用：请填写站点 ID 与控制台地址"
                : "未启用（关闭状态）");
        }
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetStatus($"已启用，正在上报 {_cfg.Id} → {_cfg.HubUrl}");
        _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _cfg.IntervalSec));
        var consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var body = BuildPayload();
                using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.HubUrl.TrimEnd('/') + "/api/report")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(_cfg.Token))
                    req.Headers.Add("X-Station-Token", _cfg.Token.Trim());

                using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    consecutiveFailures = 0;
                    SetStatus($"{DateTime.Now:HH:mm:ss} 上报成功（{_cfg.Id}）");
                }
                else
                {
                    consecutiveFailures++;
                    SetStatus($"{DateTime.Now:HH:mm:ss} 控制台返回 {(int)resp.StatusCode}，稍后重试");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 断网 / 服务器没开 / 地址写错 —— 都属于"正常可能发生"，只记不抛
                consecutiveFailures++;
                SetStatus($"{DateTime.Now:HH:mm:ss} 连不上控制台（第 {consecutiveFailures} 次）：{ex.Message}");
                if (consecutiveFailures == 1 || consecutiveFailures % 12 == 0)
                    AppLog.Warn("Station", $"上报失败（{consecutiveFailures} 次）：{ex.Message}");
            }

            // 连续失败时退避，最多 60 秒；成功则按配置间隔
            var wait = consecutiveFailures == 0
                ? interval
                : TimeSpan.FromSeconds(Math.Min(60, Math.Max(5, _cfg.IntervalSec) * Math.Min(6, consecutiveFailures + 1)));
            try { await Task.Delay(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private string BuildPayload()
    {
        StationSnapshot s;
        try { s = _snapshot(); }
        catch (Exception ex)
        {
            AppLog.Warn("Station", "采集状态失败，用空快照：" + ex.Message);
            s = new StationSnapshot("idle", "", "", 0, 0, 0, 0, 0, 0);
        }

        var payload = new
        {
            stationId = _cfg.Id.Trim(),
            name = string.IsNullOrWhiteSpace(_cfg.Name) ? _cfg.Id.Trim() : _cfg.Name.Trim(),
            version = AppVersion(),
            state = s.State,
            plan = s.Plan,
            step = s.Step,
            progress = Math.Round(Math.Clamp(s.Progress, 0, 1), 4),
            alarms = s.Alarms,
            slots = new { total = s.SlotTotal, ok = s.SlotOk, warn = s.SlotWarn, err = s.SlotErr },
            extra = new { note = s.Note, machine = Environment.MachineName },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string AppVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "" : $"V{v.Major}.{v.Minor}.{v.Build}";
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        try { StatusChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
