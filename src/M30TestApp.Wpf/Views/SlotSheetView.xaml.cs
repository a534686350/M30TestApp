using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Wpf.Views;

/// <summary>表格里的一行（字段名与 slots.html 的 COLUMNS key 对应）。</summary>
public sealed class SlotSheetRow
{
    public string slot { get; set; } = "";
    public string serialNo { get; set; } = "";
    public string positionX { get; set; } = "";
    public string positionY { get; set; } = "";
    public string board { get; set; } = "";
    public string boardSlotNo { get; set; } = "";
    public string fixture { get; set; } = "";
    public string fixtureSlotNo { get; set; } = "";
    public string valve { get; set; } = "";
    public string valveAddr { get; set; } = "";
    public string pressureController { get; set; } = "";
    public string dmm { get; set; } = "";
    public string channel { get; set; } = "";
    public string layer { get; set; } = "";
}

/// <summary>
/// 工位表的电子表格控件（WebView2 + x-spreadsheet）。
///
/// 为什么不用 DataGrid：老板要的是"和 Excel 一模一样"——单元格选择、拖动填充柄、
/// 从 Excel 直接粘一片、右键菜单，这些 WPF DataGrid 都做不到。
/// x-spreadsheet 是 MIT 许可，离线打包在 Web\sheet\ 下，不依赖 CDN。
/// </summary>
public partial class SlotSheetView : UserControl
{
    private WebView2? _web;
    private bool _ready;
    private string? _pendingJson;

    /// <summary>用户在表格里改完（防抖 350ms 后）回调，参数是整表数据。</summary>
    public event EventHandler<IReadOnlyList<SlotSheetRow>>? RowsChanged;

    public bool IsReady => _ready;

    public SlotSheetView()
    {
        InitializeComponent();
        _web = Web;
        LastInstance = this;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            // 切页签回来时容器尺寸可能变了，让表格重算一遍
            Refit();
            return;
        }
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 运行时检查：现场老机器可能没装 WebView2 Runtime
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("未检测到 WebView2 运行时");

            var sheetDir = Path.Combine(AppPaths.BaseDir, "Web", "sheet");
            if (!File.Exists(Path.Combine(sheetDir, "slots.html")))
                throw new FileNotFoundException($"表格页面缺失：{sheetDir}");

            // 用户数据目录放 LocalAppData，避免程序装在 Program Files 时无写权限
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "M30TestApp", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);

            // 用虚拟域名映射本地目录，避免 file:// 的各种限制
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "m30.sheet", sheetDir, CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    AppLog.Warn("Sheet", $"表格页面加载失败：{args.WebErrorStatus}");
                    ShowHint("表格页面加载失败", args.WebErrorStatus.ToString());
                }
            };

            // 带版本参数，避免 WebView2 用缓存的旧页面
            // 主题跟着程序走：深色时页面用反转配色，避免中间一块白板
            // 直接看当前主题的背景色亮度判断深浅（比读 ini 可靠：主题可能被运行时切换过）
            var dark = "light";
            try
            {
                if (Application.Current.TryFindResource("BgBrush") is System.Windows.Media.SolidColorBrush bg
                    && bg.Color.R < 80 && bg.Color.G < 80)
                    dark = "dark";
            }
            catch { /* 取不到按浅色 */ }
            Web.Source = new Uri($"https://m30.sheet/slots.html?theme={dark}&v={DateTime.Now.Ticks}");
            Web.Visibility = Visibility.Visible;
            AppLog.Info("Sheet", $"电子表格控件已初始化（WebView2 {version}）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sheet", $"电子表格控件初始化失败：{ex.Message}");
            ShowHint("表格控件不可用",
                "需要 Microsoft Edge WebView2 运行时。请安装后重启程序：\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message);
        }
    }

    private void ShowHint(string title, string text)
    {
        Web.Visibility = Visibility.Collapsed;
        HintPanel.Visibility = Visibility.Visible;
        HintTitle.Text = title;
        HintText.Text = text;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            if (type == "ready")
            {
                _ready = true;
                if (_pendingJson is not null)
                {
                    PushToWeb(_pendingJson);
                    _pendingJson = null;
                }
                return;
            }

            if (type == "error")
            {
                var msg = root.TryGetProperty("payload", out var p) ? p.ToString() : "(无信息)";
                AppLog.Warn("Sheet", $"页面脚本错误：{msg}");
                return;
            }

            if (type == "changed")
            {
                if (!root.TryGetProperty("payload", out var payload)) return;

                var rawJson = payload.GetRawText();
                var rows = JsonSerializer.Deserialize<List<SlotSheetRow>>(
                    rawJson, JsonOpts) ?? new List<SlotSheetRow>();
                RowsChanged?.Invoke(this, rows);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sheet", $"处理表格消息失败：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>把工位数据灌进表格（不触发回写回调）。</summary>
    public void LoadSlots(IReadOnlyList<SlotEntry> slots)
    {
        var rows = slots.Select(s => new SlotSheetRow
        {
            slot = s.Slot,
            serialNo = s.SerialNo,
            positionX = s.PositionX,
            positionY = s.PositionY,
            board = s.Board,
            boardSlotNo = s.BoardSlotNo,
            fixture = s.Fixture,
            fixtureSlotNo = s.FixtureSlotNo,
            valve = s.Valve,
            valveAddr = s.ValveAddr,
            pressureController = s.PressureController,
            dmm = s.Dmm,
            channel = s.Channel,
            layer = s.Layer,
        }).ToList();

        var json = JsonSerializer.Serialize(rows, JsonOpts);
        if (!_ready)
        {
            _pendingJson = json;   // 页面还没 ready，先存着
            return;
        }
        PushToWeb(json);
    }

    private void PushToWeb(string json)
    {
        if (_web?.CoreWebView2 is null) return;

        // 把 JSON 再序列化一次变成「JS 字符串字面量」，页面里 JSON.parse 还原。
        // 直接拼对象字面量遇到转义/长度问题会静默失败（实测 load 不执行）。
        var literal = JsonSerializer.Serialize(json);
        var script = $"window.sheetBridge && window.sheetBridge.loadJson({literal});";
        _web.CoreWebView2.ExecuteScriptAsync(script).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Warn("Sheet", $"推送数据失败：{t.Exception?.GetBaseException().Message}");
        });
    }

    /// <summary>让表格重新贴合容器尺寸（切换页签后调用）。</summary>
    public void Refit()
    {
        try { _web?.CoreWebView2?.ExecuteScriptAsync("window.sheetBridge && window.sheetBridge.refit && window.sheetBridge.refit();"); }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 把当前表格画面截成 PNG。普通 RenderTargetBitmap 截不到 WebView2（独立进程渲染），
    /// 自检时必须用 CoreWebView2 自己的 CapturePreviewAsync。
    /// </summary>
    public async Task<bool> CaptureAsync(string path)
    {
        if (_web?.CoreWebView2 is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var fs = File.Create(path);
            await _web.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, fs);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sheet", $"表格截图失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>自检用：模拟改一个单元格，看回写链路是否通。</summary>
    public async Task SetCellForTestAsync(int row, int col, string value)
    {
        if (_web?.CoreWebView2 is null) return;
        var v = System.Text.Json.JsonSerializer.Serialize(value);
        await _web.CoreWebView2.ExecuteScriptAsync(
            $"window.sheetBridge && window.sheetBridge.setCell({row}, {col}, {v});");
    }

    /// <summary>最近一个实例（自检钩子用）。</summary>
    public static SlotSheetView? LastInstance { get; private set; }
}
