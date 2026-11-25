using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using M30TestApp.Core;
using M30TestApp.Core.Config;
using M30TestApp.Core.Devices;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 开始测试前的设备连通性预检窗口。
/// 按本轮采集模式列出需要的设备（压力控制器 / 烘箱 / 用到的 973 / 电路板 / DAC），
/// 逐个打开并判状态；进度条 + 百分比实时更新，全过才允许点「开始测试」。
/// </summary>
public partial class PreflightWindow : Window
{
    private readonly TestSession _session;
    private readonly IniFile _settings;
    private readonly ObservableCollection<PreflightItemVm> _items = new();

    /// <summary>用户是否选择继续（检测有失败项时点「仍然开始」也算 true）。</summary>
    public bool Proceed { get; private set; }

    public PreflightWindow(TestSession session, IniFile settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        ItemsHost.ItemsSource = _items;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            SubText.Text = "预检异常：" + ex.Message;
            SubText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            ProceedButton.IsEnabled = true;
            ProceedButton.Content = "仍然开始";
        }
    }

    private async Task RunAsync()
    {
        var checks = BuildChecks();
        if (checks.Count == 0)
        {
            Bar.Value = 100;
            PercentText.Text = "100%";
            TitleText.Text = "没有需要检测的设备";
            ProceedButton.IsEnabled = true;
            return;
        }

        // 先列本轮实际检测的，再列「未启用」的（让现场能确认没漏掉/没多检）
        foreach (var item in checks.Select(c => c.Item)) _items.Add(item);
        foreach (var item in _skipped) _items.Add(item);

        var failures = new List<string>();
        using var cts = new CancellationTokenSource();

        for (var i = 0; i < checks.Count; i++)
        {
            var (item, run) = checks[i];
            item.IsRunning = true;
            item.State = "检测中";
            SubText.Text = $"正在检测：{item.Name}";
            UpdateProgress(i, checks.Count);

            string? error;
            try
            {
                error = await run();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            item.IsRunning = false;
            if (string.IsNullOrEmpty(error))
            {
                item.IsOk = true;
                item.State = "正常";
                item.Detail = "";
            }
            else
            {
                item.IsFailed = true;
                item.State = "异常";
                item.Detail = error;
                failures.Add($"{item.Name}：{error}");
            }

            UpdateProgress(i + 1, checks.Count);
        }

        Bar.Value = 100;
        PercentText.Text = "100%";

        if (failures.Count == 0)
        {
            TitleText.Text = "设备连通性检测通过";
            SubText.Text = $"共检测 {checks.Count} 项，全部正常，可以开始测试。"
                + (_skipped.Count > 0 ? $"（另有 {_skipped.Count} 项本轮未启用，已跳过）" : "");
            SubText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            ProceedButton.IsEnabled = true;
            ProceedButton.Content = "开始测试";
        }
        else
        {
            TitleText.Text = $"设备连通性检测：{failures.Count} 项异常";
            SubText.Text = "以下设备不可用，继续测试可能采不到数据或采错：" + Environment.NewLine
                + string.Join(Environment.NewLine, failures);
            SubText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            ProceedButton.IsEnabled = true;
            ProceedButton.Content = "仍然开始";
        }
    }

    private void UpdateProgress(int done, int total)
    {
        var pct = total <= 0 ? 100 : (int)Math.Round(done * 100.0 / total);
        Bar.Value = pct;
        PercentText.Text = $"{pct}%";
    }

    /// <summary>
    /// 按本轮采集模式 + 方案勾选，列出要检测的设备。
    ///
    /// 依据是「选择运行方案」窗口点「应用」后写进 Setting.ini 的
    /// <c>[Slots] UsePressure / UseOven / AutoTestMode</c>（和预检读的是同一份 ini 实例）。
    /// **没勾的设备只列出来标「未启用」，不打开、不计入进度** —— 老板不用勾烘箱就不会去连烘箱。
    /// </summary>
    private List<(PreflightItemVm Item, Func<Task<string?>> Run)> BuildChecks()
    {
        var list = new List<(PreflightItemVm, Func<Task<string?>>)>();
        var dmmMode = _settings.Get("Slots", "AutoTestMode", "Board")
            .Equals("Dmm", StringComparison.OrdinalIgnoreCase);
        var useOven = ReadBool("UseOven", true);
        var usePressure = ReadBool("UsePressure", true);

        void Add(string name, Func<Task<string?>> run) =>
            list.Add((new PreflightItemVm { Name = name }, run));

        // 不检测的设备也显示，标注「未启用」——现场一眼能确认「烘箱确实没连」
        void Skip(string name, string why) =>
            _skipped.Add(new PreflightItemVm { Name = name, State = "未启用", Detail = why });

        if (usePressure && _session.Pressure is not null)
            Add("压力控制器", () => DevicePreflight.ProbeAsync("压力控制器", _session.Pressure, CancellationToken.None));
        else
            Skip("压力控制器", "方案未勾选");

        if (useOven && _session.Oven is not null)
            Add("烘箱", () => DevicePreflight.ProbeAsync("烘箱", _session.Oven, CancellationToken.None));
        else
            Skip("烘箱", "方案未勾选");

        // 本轮工位表实际用到的 973 台
        var units = RequiredDmmUnits();
        if (units.Count == 0)
            Skip("DAQ973A", "本轮工位表未配置");
        foreach (var unit in units)
        {
            if (unit < 0 || unit >= _session.Dmms.Count)
            {
                Skip($"DAQ973A #{unit + 1}", "未配置地址");
                continue;
            }
            var dmm = _session.Dmms[unit];
            var label = $"DAQ973A #{unit + 1}";
            Add(label, () => DevicePreflight.ProbeAsync(label, dmm, CancellationToken.None));
        }

        // 电路板采集模式才需要电路板与 DAC
        if (!dmmMode)
        {
            if (_session.Board is not null)
                Add("电路板", () => DevicePreflight.ProbeAsync("电路板", _session.Board, CancellationToken.None));
            if (_session.Dac is not null)
                Add("DAC 板卡", () => DevicePreflight.ProbeAsync("DAC 板卡", _session.Dac, CancellationToken.None));
        }
        else
        {
            Skip("电路板 / DAC", "万用表采集模式不使用");
        }

        return list;
    }

    private bool ReadBool(string key, bool fallback) =>
        bool.TryParse(_settings.Get("Slots", key, ""), out var v) ? v : fallback;

    /// <summary>本轮不检测的设备（显示在列表里标「未启用」）。</summary>
    private readonly List<PreflightItemVm> _skipped = new();

    private List<int> RequiredDmmUnits()
    {
        var set = new SortedSet<int>();
        foreach (var slot in _session.Slots.Entries)
        {
            var n = SlotDacAddress.ParseSlotIndex(slot.Slot);
            if (n > 0) set.Add(DmmAutoSlotMap.UnitIndexForSlot(n));
        }
        return set.ToList();
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        Proceed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Proceed = false;
        DialogResult = false;
        Close();
    }
}
