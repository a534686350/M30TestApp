using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class ConfigView : UserControl
{
    private DateTime _lastApply = DateTime.MinValue;
    private int _scanSlotIndex;
    private bool _scanPositionInitialized;
    private bool _syncingFromSheet;

    private static readonly Brush StatusOkBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush StatusWarnBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    private ConfigViewModel? Vm => DataContext as ConfigViewModel;
    private IReadOnlyList<SlotEntry> SlotList => Vm?.Slots ?? (IReadOnlyList<SlotEntry>)Array.Empty<SlotEntry>();

    public ConfigView()
    {
        InitializeComponent();

        // 电子表格改完（防抖 350ms）→ 回写 SlotEntry + 落盘
        SlotSheet.RowsChanged += OnSheetRowsChanged;

        // 工位表本身被重建（刷新工位表 / 改工位数量）→ 重新灌进表格
        DataContextChanged += (_, _) => HookSlotCollection();

        // 切换 Tab 回来时保留扫码进度
        Loaded += (_, _) =>
        {
            HookSlotCollection();
            if (!_scanPositionInitialized)
            {
                _scanPositionInitialized = true;
                ResetScanSlot();
                return;
            }

            var count = SlotList.Count;
            if (count == 0) _scanSlotIndex = 0;
            else if (_scanSlotIndex >= count) _scanSlotIndex = count - 1;

            UpdateScanStatusLabels();
        };
    }

    private void HookSlotCollection()
    {
        if (Vm is not { } vm) return;
        vm.Slots.CollectionChanged -= OnSlotsCollectionChanged;
        vm.Slots.CollectionChanged += OnSlotsCollectionChanged;
        PushSlotsToSheet();
    }

    private void OnSlotsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => PushSlotsToSheet();

    /// <summary>把 Config.Slots 灌进电子表格（不触发回写）。</summary>
    private void PushSlotsToSheet()
    {
        if (Vm is not { } vm) return;
        _syncingFromSheet = true;
        try
        {
            SlotSheet.LoadSlots(vm.Slots.ToList());
        }
        finally
        {
            _syncingFromSheet = false;
        }
        UpdateScanStatusLabels();
    }

    /// <summary>表格改完 → 回写工位对象。工位号是主键，不回写；按工位号匹配而不是按下标（表格可能被排序/留空行）。</summary>
    private void OnSheetRowsChanged(object? sender, IReadOnlyList<SlotSheetRow> rows)
    {
        if (_syncingFromSheet) return;
        if (Vm is not { } vm) return;
        if (rows.Count == 0) return;

        // 保护：整批读回来全是空值说明表格没加载好，绝不能拿去覆盖内存数据
        if (rows.All(r => string.IsNullOrWhiteSpace(r.serialNo)
                          && string.IsNullOrWhiteSpace(r.board)
                          && string.IsNullOrWhiteSpace(r.channel)))
        {
            AppLog.Warn("UI", "工位表读回全空，已忽略本次回写");
            return;
        }

        // 明显错位保护：序列号变成工位号（Slot1/Slot2…）说明表格列被拖乱了，拒绝回写。
        // 之前就是这类错位把 CSV 里 9 行序列号写成了 "Slot1"。
        if (rows.Any(r => r.serialNo.StartsWith("Slot", StringComparison.Ordinal)
                          && r.serialNo.Length > 4 && char.IsDigit(r.serialNo[4])))
        {
            AppLog.Warn("UI", "工位表序列号疑似错位（值为工位号），已忽略本次回写");
            return;
        }

        // 大面积清空保护：粘贴一个空单元格到 19 行会把序列号整片抹掉（老板踩过）。
        // 空值比原来多 3 个以上就认为是误操作，拒绝回写并把表格恢复原样。
        var emptyNow = rows.Count(r => string.IsNullOrWhiteSpace(r.serialNo));
        var emptyBefore = vm.Slots.Count(s => string.IsNullOrWhiteSpace(s.SerialNo));
        if (emptyNow > emptyBefore + 3)
        {
            AppLog.Warn("UI", $"序列号被大面积清空（{emptyBefore} → {emptyNow} 个空），已拒绝回写");
            PushSlotsToSheet();
            if (Vm is { } v0) v0.StatusMessage = "检测到序列号被大面积清空，已撤销（要清空请逐行删）";
            return;
        }

        var bySlot = new Dictionary<string, SlotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in vm.Slots)
            if (!string.IsNullOrWhiteSpace(s.Slot)) bySlot[s.Slot] = s;

        var changed = 0;
        foreach (var src in rows)
        {
            if (string.IsNullOrWhiteSpace(src.slot)) continue;
            if (!bySlot.TryGetValue(src.slot, out var dst)) continue;

            if (dst.SerialNo != src.serialNo) { dst.SerialNo = src.serialNo; changed++; }
            if (dst.PositionX != src.positionX) { dst.PositionX = src.positionX; changed++; }
            if (dst.PositionY != src.positionY) { dst.PositionY = src.positionY; changed++; }
            if (dst.Board != src.board) { dst.Board = src.board; changed++; }
            if (dst.BoardSlotNo != src.boardSlotNo) { dst.BoardSlotNo = src.boardSlotNo; changed++; }
            if (dst.Fixture != src.fixture) { dst.Fixture = src.fixture; changed++; }
            if (dst.FixtureSlotNo != src.fixtureSlotNo) { dst.FixtureSlotNo = src.fixtureSlotNo; changed++; }
            if (dst.Valve != src.valve) { dst.Valve = src.valve; changed++; }
            if (dst.ValveAddr != src.valveAddr) { dst.ValveAddr = src.valveAddr; changed++; }
            if (dst.PressureController != src.pressureController) { dst.PressureController = src.pressureController; changed++; }
            if (dst.Dmm != src.dmm) { dst.Dmm = src.dmm; changed++; }
            if (dst.Channel != src.channel) { dst.Channel = src.channel; changed++; }
            if (dst.Layer != src.layer) { dst.Layer = src.layer; changed++; }
        }

        if (changed == 0) return;

        vm.PersistSlotsAfterEdit();
        vm.StatusMessage = $"已保存 {changed} 处修改";
        UpdateScanStatusLabels();
        AppLog.Info("UI", $"工位表改动已保存（{changed} 处）");
    }

    public void EnableSlotsOnlyMode()
    {
        foreach (var item in ConfigTabs.Items)
        {
            if (item is TabItem tab)
                tab.Visibility = string.Equals(tab.Header?.ToString(), "工位", StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        ConfigTabs.SelectedValue = "工位";
        OpenSlotsWindowButton.Visibility = Visibility.Collapsed;
    }

    private void OpenSlotsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        var owner = Window.GetWindow(this);
        if (owner is null) return;

        var window = new SlotsWindow(vm);
        window.Closed += (_, _) =>
        {
            if (!owner.IsLoaded) return;
            owner.Show();
            owner.Activate();
        };

        owner.Hide();
        window.Show();
        window.Activate();
    }

    private void ResetScanSlot()
    {
        _scanSlotIndex = 0;
        UpdateScanStatusLabels();
    }

    /// <summary>更新「下一工位」徽标与「已扫/共」进度。</summary>
    private void UpdateScanStatusLabels()
    {
        var list = SlotList;
        var total = list.Count;
        var scanned = 0;
        for (var i = 0; i < total; i++)
            if (!string.IsNullOrEmpty(list[i].SerialNo)) scanned++;

        ScanProgressLabel.Text = total == 0 ? "" : $"已扫 {scanned} / 共 {total}";

        var nextIndex = total == 0 ? -1 : Math.Min(_scanSlotIndex, total - 1);
        for (var i = 0; i < total; i++)
            list[i].IsNext = i == nextIndex;

        if (total == 0) ScanSlotLabel.Text = "-";
        else if (_scanSlotIndex >= total) ScanSlotLabel.Text = "已完成";
        else ScanSlotLabel.Text = list[_scanSlotIndex].Slot;
    }

    private int CurrentVisibleScanIndex()
    {
        var count = SlotList.Count;
        if (count == 0) return -1;
        return Math.Clamp(_scanSlotIndex, 0, count - 1);
    }


    // ─── 上报设备 ID（站点标识）管理员锁 ────────────────────────────────────
    //
    // 站点标识 = 本机在总控制台上的身份，被随手改掉会让控制台把两台设备当成一台，
    // 所以默认锁住，要改先输管理员密码（默认 123..00）。
    // PasswordBox 出于安全设计不支持绑定，只能在 code-behind 里取值校验。
    private void OnStationUnlockClick(object sender, RoutedEventArgs e) => TryUnlockStation();

    private void OnStationPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        TryUnlockStation();
    }

    private void TryUnlockStation()
    {
        if (DataContext is not ConfigViewModel vm || vm.Settings is null) return;

        if (vm.Settings.TryUnlockStation(StationPasswordBox.Password))
        {
            StationPasswordBox.Clear();
            return;
        }

        var owner = Window.GetWindow(this);
        const string message = "管理员密码不正确，站点标识保持锁定。";
        if (owner is not null)
            AppDialog.Show(owner, message, "上报设备 ID", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppDialog.Show(message, "上报设备 ID", MessageBoxButton.OK, MessageBoxImage.Warning);

        StationPasswordBox.SelectAll();
        StationPasswordBox.Focus();
    }

    /// <summary>「打开在线工作台」：在默认浏览器打开总控制台（地址程序内置）。</summary>
    private void OnOpenStationHubClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm || vm.Settings is null) return;

        var url = vm.Settings.StationHubUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            M30TestApp.Core.Common.AppLog.Warn("Settings", "控制台地址为空，无法打开在线工作台");
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            M30TestApp.Core.Common.AppLog.Warn("Settings", "打开在线工作台失败：" + ex.Message);
        }
    }

    private void OnBarcodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            ApplyBarcode();
            e.Handled = true;
        }
    }

    private void OnBarcodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        var txt = tb.Text;
        if (txt.Contains('\r') || txt.Contains('\n'))
        {
            tb.Text = txt.Replace("\r", "").Replace("\n", "");
            tb.CaretIndex = tb.Text.Length;
            ApplyBarcode();
        }
    }

    private void OnBarcodeConfirm(object sender, RoutedEventArgs e) => ApplyBarcode();

    /// <summary>
    /// 「清空重扫」：清除全部已录入的序列号并回到第 1 行。
    /// 原先只把游标归零、不动数据，用户点了没反应；现在真清，且清空前二次确认。
    /// </summary>
    private void OnScanResetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;

        var total = vm.Slots.Count;
        var scanned = vm.Slots.Count(s => !string.IsNullOrEmpty(s.SerialNo));

        // 没有已录入内容：只需回到第 1 行，不必打扰用户。
        if (scanned == 0)
        {
            ResetScanSlot();
            ScanStatusText.Text = total == 0 ? "没有工位" : "没有已录入的序列号，已回到第 1 行";
            ScanStatusText.Foreground = StatusOkBrush;
            BarcodeInput.Clear();
            BarcodeInput.Focus();
            return;
        }

        var result = AppDialog.Show(
            $"将清除已录入的 {scanned} 个序列号（共 {total} 个工位），并回到第 1 行。\n\n" +
            "此操作不可撤销，确定清空吗？",
            "清空重扫",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            BarcodeInput.Focus();
            return;
        }

        foreach (var slot in vm.Slots)
            slot.SerialNo = "";

        ResetScanSlot();
        ScanStatusText.Text = $"已清空 {scanned} 个序列号，回到第 1 行";
        ScanStatusText.Foreground = StatusOkBrush;
        BarcodeInput.Clear();
        BarcodeInput.Focus();
    }

    private void ApplyBarcode()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastApply).TotalMilliseconds < 50) return;

        var barcode = BarcodeInput.Text.Trim();
        if (string.IsNullOrEmpty(barcode)) return;
        _lastApply = now;

        if (DataContext is not ConfigViewModel vm) return;

        // 扫到表格末尾时按需扩展一行（保持扫码连续，不用先去改数量）。
        if (_scanSlotIndex >= vm.Slots.Count)
            vm.EnsureSlotCount(_scanSlotIndex + 1);

        _scanSlotIndex = Math.Clamp(_scanSlotIndex, 0, Math.Max(0, vm.Slots.Count - 1));
        if (vm.Slots.Count == 0) return;
        var slot = vm.Slots[_scanSlotIndex];

        // 序列号允许重复（同一编号可能出现在多个工位，例如同批传感器复测）。
        // 重复只提示不拦截 —— 写进去之后仍会照常往后走，避免现场扫到重复就卡住。
        var duplicateOf = "";
        for (var i = 0; i < vm.Slots.Count; i++)
        {
            if (i == _scanSlotIndex) continue;
            if (string.Equals(vm.Slots[i].SerialNo, barcode, StringComparison.OrdinalIgnoreCase))
            {
                duplicateOf = vm.Slots[i].Slot;
                break;
            }
        }

        slot.SerialNo = barcode;
        vm.PersistSlotsAfterEdit();

        ScanStatusText.Text = duplicateOf.Length > 0
            ? $"{slot.Slot} ← {barcode}（与 {duplicateOf} 重复，已允许）"
            : $"{slot.Slot} ← {barcode}";
        ScanStatusText.Foreground = duplicateOf.Length > 0 ? StatusWarnBrush : StatusOkBrush;

        _scanSlotIndex++;

        // 把新值同步进电子表格（扫码是程序改数据，不能触发回写）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            PushSlotsToSheet();
            UpdateScanStatusLabels();
        });

        BarcodeInput.Clear();
        BarcodeInput.Focus();
    }
}
