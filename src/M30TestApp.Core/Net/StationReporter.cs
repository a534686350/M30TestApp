using System;
using System.Security.Cryptography;
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

    // 现场不需要手填这些固定的控制台参数；管理员仍可在 Setting.ini 覆盖。
    public const string DefaultHubUrl = "http://54.206.27.158/";
    public const string DefaultToken = "lzJq06lbJrVT9MZQ8kv3GOAd";

    /// <summary>站点唯一 ID。为空时按电脑自动生成稳定 ID。</summary>
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

    public static StationConfig Load(IniFile ini)
    {
        var id = ini.Get(Section, "Id", "").Trim();
        var generated = string.IsNullOrWhiteSpace(id);
        if (generated) id = GenerateDefaultId();

        return new StationConfig
        {
            Id = id,
            Name = ini.Get(Section, "Name", ""),
            HubUrl = ini.Get(Section, "HubUrl", DefaultHubUrl),
            Token = ini.Get(Section, "Token", DefaultToken),
            // 新装设备默认启用上报；已有配置明确写 0 时保持关闭。
            Enabled = ini.Get(Section, "Enabled", "1") is "1" or "true" or "True",
            IntervalSec = int.TryParse(ini.Get(Section, "IntervalSec", "5"), out var n) ? Math.Max(5, n) : 5,
        };
    }

    /// <summary>
    /// 没填站点 ID 时按机器生成一个稳定 ID：同一台电脑每次启动不变，
    /// 不依赖 IP、不上传硬件序列号；用户解锁后仍可手动改成车间/工位昵称。
    /// </summary>
    public static string GenerateDefaultId()
    {
        var machine = string.IsNullOrWhiteSpace(Environment.MachineName) ? "PC" : Environment.MachineName.Trim();
        var safe = new string(machine.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = "PC";
        if (safe.Length > 20) safe = safe[..20];

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machine.ToUpperInvariant()));
        var suffix = Convert.ToHexString(digest, 0, 3);
        return $"M30-{safe}-{suffix}";
    }

    public void SaveTo(IniFile ini)
    {
        ini.Set(Section, "Id", Id?.Trim() ?? "");
        ini.Set(Section, "Name", Name?.Trim() ?? "");
        // 地址 / 令牌由程序内置：为空就回填默认值，绝不让空值覆盖成"没配置"
        ini.Set(Section, "HubUrl", string.IsNullOrWhiteSpace(HubUrl) ? DefaultHubUrl : HubUrl.Trim());
        ini.Set(Section, "Token", string.IsNullOrWhiteSpace(Token) ? DefaultToken : Token.Trim());
        ini.Set(Section, "Enabled", Enabled ? "1" : "0");
        ini.Set(Section, "IntervalSec", Math.Max(5, IntervalSec).ToString());
    }
}

/// <summary>上报给总控制台的一个工位条目。</summary>
public sealed record StationSlotInfo(string Slot, string Serial, string State, string Note = "");

/// <summary>上报给总控制台的一条测试指标。</summary>
public sealed record StationMetricInfo(string Name, string Value, string Limit = "", string Result = "");

/// <summary>上报给总控制台的一份状态快照。</summary>
/// <remarks>
/// 前 10 个参数是第一期就有的位置参数，后面的是第二期加的明细字段（都有默认值，老调用不受影响）。
/// Slots / Metrics 为 null 表示"本次不带"，服务端会沿用上一份，省带宽。
/// </remarks>
public sealed record StationSnapshot(
    string State, string Plan, string Step, double Progress, int Alarms,
    int SlotTotal, int SlotOk, int SlotWarn, int SlotErr, string? Note = null,
    string Model = "",          // 传感器型号
    string Batch = "",          // 批次号（Setting.ini [Slots] BatchNo）
    string StartedAt = "",      // 本次运行开始时间 yyyy-MM-dd HH:mm:ss
    string Elapsed = "",        // 已运行时长 HH:mm:ss
    string TempPoint = "",      // 当前温度点
    string PressurePoint = "",  // 当前压力点
    IReadOnlyList<StationSlotInfo>? Slots = null,      // 工位明细
    IReadOnlyList<StationMetricInfo>? Metrics = null); // 指标结果

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
        _lastSlotFingerprint = "";      // 重新开始上报时明细必须重发一遍
        _lastMetricsFingerprint = "";
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

    /// <summary>上一轮发出去的工位明细指纹；没变就不再发（256 工位全量发一遍不小）。</summary>
    private string _lastSlotFingerprint = "";
    private string _lastMetricsFingerprint = "";

    private string BuildPayload()
    {
        StationSnapshot s;
        try { s = _snapshot(); }
        catch (Exception ex)
        {
            AppLog.Warn("Station", "采集状态失败，用空快照：" + ex.Message);
            s = new StationSnapshot("idle", "", "", 0, 0, 0, 0, 0, 0);
        }

        // 明细只在变化时带，其余心跳发 null（服务端沿用上一份）
        object? slotDetails = null;
        if (s.Slots is { Count: > 0 } slots)
        {
            var fp = Fingerprint(slots.Select(x => $"{x.Slot}|{x.Serial}|{x.State}|{x.Note}"));
            if (fp != _lastSlotFingerprint)
            {
                _lastSlotFingerprint = fp;
                slotDetails = slots.Select(x => new { slot = x.Slot, serial = x.Serial, state = x.State, note = x.Note }).ToList();
            }
        }

        object? metrics = null;
        if (s.Metrics is { Count: > 0 } ms)
        {
            var fp = Fingerprint(ms.Select(x => $"{x.Name}|{x.Value}|{x.Limit}|{x.Result}"));
            if (fp != _lastMetricsFingerprint)
            {
                _lastMetricsFingerprint = fp;
                metrics = ms.Select(x => new { name = x.Name, value = x.Value, limit = x.Limit, result = x.Result }).ToList();
            }
        }

        var payload = new
        {
            stationId = _cfg.Id.Trim(),
            name = string.IsNullOrWhiteSpace(_cfg.Name) ? _cfg.Id.Trim() : _cfg.Name.Trim(),
            version = AppVersion(),
            state = s.State,
            plan = s.Plan,
            model = s.Model,
            batch = s.Batch,
            step = s.Step,
            startedAt = s.StartedAt,
            elapsed = s.Elapsed,
            tempPoint = s.TempPoint,
            pressurePoint = s.PressurePoint,
            progress = Math.Round(Math.Clamp(s.Progress, 0, 1), 4),
            alarms = s.Alarms,
            slots = new { total = s.SlotTotal, ok = s.SlotOk, warn = s.SlotWarn, err = s.SlotErr },
            slotDetails,
            metrics,
            extra = new { note = s.Note, machine = Environment.MachineName },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>给明细算个短指纹，只用来判断"变没变"，不进网络。</summary>
    private static string Fingerprint(IEnumerable<string> parts)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(bytes, 0, 8);
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
