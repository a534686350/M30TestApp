using System;
using System.Collections.ObjectModel;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

// ConfigViewModel 的工位布局部分（partial）。
// 字段存储与绑定通知保留在本类；板卡映射公式、批量生成入参、Setting.ini 读写
// 统一委托 Core.Config.SlotLayoutSnapshot（与 RunSetupViewModel 共用同一实现）。
public sealed partial class ConfigViewModel : ViewModelBase
{
    // ── 工位 ────────────────────────────────────────────────────────────
    public const int SlotMax = SlotLayoutHelper.SlotMax;
    public ObservableCollection<SlotEntry> Slots { get; } = new();
    public int PreviewCount => Slots.Count;

    // ── 973 台数自动判读 ────────────────────────────────────────────────
    // 每台 973 管 18 个工位：1-18 → 1 台，19-36 → 2 台，37-54 → 3 台。
    // 工位页直接写清楚，配错时红字提示，避免「54 个工位却只启用了 1 台 973」。

    /// <summary>本工位表实际需要的 973 台数（按最大工位号推导）。</summary>
    public int RequiredDmmUnits
    {
        get
        {
            var maxSlotNo = 0;
            foreach (var s in Slots)
            {
                var n = SlotDacAddress.ParseSlotIndex(s.Slot);
                if (n > maxSlotNo) maxSlotNo = n;
            }
            return maxSlotNo == 0 ? 0 : DmmAutoSlotMap.UnitIndexForSlot(maxSlotNo) + 1;
        }
    }

    /// <summary>当前设备配置里实际启用的 973 台数。</summary>
    public int ConfiguredDmmUnits => _session.Dmms.Count;

    /// <summary>配置台数不足（工位数超出已启用 973 的覆盖范围）。</summary>
    public bool DmmUnitShortage => RequiredDmmUnits > ConfiguredDmmUnits;

    /// <summary>工位页顶部的 973 台数说明。</summary>
    public string DmmUnitHint
    {
        get
        {
            var per = Math.Max(1, DmmAutoSlotMap.SlotsPerUnit);
            if (Slots.Count == 0) return $"每台 DAQ973A 管 {per} 个工位";

            var need = RequiredDmmUnits;
            var have = ConfiguredDmmUnits;
            var text = $"{Slots.Count} 个工位需要 {need} 台 DAQ973A（每台 {per} 个），当前已启用 {have} 台";
            return DmmUnitShortage
                ? text + $" —— 不足 {need - have} 台，请到「配置 → 设备」勾选并填地址"
                : text;
        }
    }

    private string _slotEditStatus = "";
    /// <summary>工位表就地编辑/批量粘贴后的操作反馈（显示在工位页标题栏）。</summary>
    public string StatusMessage
    {
        get => _slotEditStatus;
        set => SetField(ref _slotEditStatus, value);
    }

    /// <summary>工位表里每个工位对应的 973 台号文本，如 "973-1"。供列显示/校验用。</summary>
    public static string DmmUnitLabelForSlot(string slotName)
    {
        var n = SlotDacAddress.ParseSlotIndex(slotName);
        if (n <= 0) return "-";
        return $"973-{DmmAutoSlotMap.UnitIndexForSlot(n) + 1}";
    }

    private void NotifyDmmUnitInfo()
    {
        OnPropertyChanged(nameof(RequiredDmmUnits));
        OnPropertyChanged(nameof(ConfiguredDmmUnits));
        OnPropertyChanged(nameof(DmmUnitShortage));
        OnPropertyChanged(nameof(DmmUnitHint));
    }

    private int _slotCount = 16;
    public int SlotCount
    {
        get => _slotCount;
        set => SetSlotLayoutField(ref _slotCount, Math.Clamp(value, 1, MaxSlotCountFromStart));
    }

    private string _batchNo = $"{DateTime.Now:yyMMdd}_01";
    public string BatchNo { get => _batchNo; set => SetSlotLayoutField(ref _batchNo, value); }

    private int _startIndex = 1;
    public int StartIndex
    {
        get => _startIndex;
        set
        {
            var next = Math.Clamp(value, 1, SlotMax);
            if (!SetSlotLayoutField(ref _startIndex, next)) return;
            if (_slotCount > MaxSlotCountFromStart)
            {
                _slotCount = MaxSlotCountFromStart;
                OnPropertyChanged(nameof(SlotCount));
            }
            SyncDerivedLayoutFromStartIndex();
        }
    }

    /// <summary>从起始工位到工位上限还能生成多少个工位。</summary>
    private int MaxSlotCountFromStart =>
        Math.Max(1, SlotMax - Math.Clamp(_startIndex, 1, SlotMax) + 1);

    /// <summary>
    /// 起始阀位 / 起始板卡 / 起始板卡工位由起始工位推导，与批量生成、运行设置、运行时同源：
    /// 阀门每 32 工位一个（起始 129 → 5 号阀），板卡每 BoardSlotCapacity 工位一块
    /// （起始 129、每板 16 工位 → 板卡 9 / 工位 1）。
    /// </summary>
    private void SyncDerivedLayoutFromStartIndex()
    {
        var zeroBased = Math.Max(0, _startIndex - 1);

        var valve = SlotLayoutHelper.ValveForSlot(_startIndex);
        if (_startValve != valve)
        {
            _startValve = valve;
            OnPropertyChanged(nameof(StartValve));
        }

        var capacity = Math.Max(1, _boardSlotCapacity);
        var board = zeroBased / capacity + 1;
        var boardSlot = zeroBased % capacity + 1;
        if (_startBoard != board)
        {
            _startBoard = board;
            OnPropertyChanged(nameof(StartBoard));
        }
        if (_startBoardSlot != boardSlot)
        {
            _startBoardSlot = boardSlot;
            OnPropertyChanged(nameof(StartBoardSlot));
        }
    }

    private int _startBoard = 1;
    public int StartBoard { get => _startBoard; set => SetSlotLayoutField(ref _startBoard, value); }

    private int _startBoardSlot = 1;
    public int StartBoardSlot { get => _startBoardSlot; set => SetSlotLayoutField(ref _startBoardSlot, value); }

    private int _boardSlotCapacity = 16;
    public int BoardSlotCapacity { get => _boardSlotCapacity; set => SetSlotLayoutField(ref _boardSlotCapacity, Math.Max(1, value)); }

    private int _startValve = 1;
    public int StartValve { get => _startValve; set => SetSlotLayoutField(ref _startValve, value); }

    private int _fixtureSlotCapacity = 8;
    public int FixtureSlotCapacity { get => _fixtureSlotCapacity; set => SetSlotLayoutField(ref _fixtureSlotCapacity, Math.Max(1, value)); }

    private int _fixtureCount = 8;
    public int FixtureCount { get => _fixtureCount; set => SetSlotLayoutField(ref _fixtureCount, Math.Max(1, value)); }

    private int _startChannel = 1;
    public int StartChannel { get => _startChannel; set => SetSlotLayoutField(ref _startChannel, value); }

    /// <summary>当前布局参数快照（供公式计算 / 批量生成 / ini 持久化共用）。</summary>
    internal SlotLayoutSnapshot CurrentSlotLayout => new(
        SlotCount: _slotCount,
        BatchNo: _batchNo,
        StartIndex: _startIndex,
        StartBoard: _startBoard,
        StartBoardSlot: _startBoardSlot,
        BoardSlotCapacity: _boardSlotCapacity,
        StartValve: _startValve,
        FixtureSlotCapacity: _fixtureSlotCapacity,
        FixtureCount: _fixtureCount,
        StartChannel: _startChannel,
        StartSerial: _startSerial,
        AutoNumber: _autoNumber);

    public int BoardCount => CurrentSlotLayout.BoardCount;
    public int LastBoard => CurrentSlotLayout.LastBoard;
    public int LastBoardSlot => CurrentSlotLayout.LastBoardSlot;
    public string BoardMappingSummary => CurrentSlotLayout.BoardMappingSummary;

    private int _startSerial = 1;
    public int StartSerial { get => _startSerial; set => SetSlotLayoutField(ref _startSerial, value); }

    private bool _autoNumber = true;
    public bool AutoNumber { get => _autoNumber; set => SetSlotLayoutField(ref _autoNumber, value); }

    /// <summary>扫码录入需要下一行时扩展，不预生成占位 DEMO 行。</summary>
    public void EnsureSlotCount(int count)
    {
        var target = Math.Clamp(count, 1, SlotMax);
        if (target <= Slots.Count) return;
        SlotCount = target;
    }

    public RelayCommand RegenerateSlotsCommand { get; private set; } = null!;
}
