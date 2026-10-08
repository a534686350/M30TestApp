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
