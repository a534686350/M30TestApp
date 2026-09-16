using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace M30TestApp.Core.Config;

public sealed record SlotEntry(
    string Slot,
    string SerialNo,
    string Valve,
    string Board,
    string BoardSlotNo,
    string Layer,
    string Fixture,
    string FixtureSlotNo,
    string PressureController,
    string Dmm,
    string Channel,
    string ValveAddr,
    string PositionX = "",
    string PositionY = "") : INotifyPropertyChanged
{
    private string _serialNo = SerialNo;

    /// <summary>Allow barcode scanner to update SerialNo after construction. Raises PropertyChanged so DataGrid refreshes without Items.Refresh().</summary>
    public string SerialNo
    {
        get => _serialNo;
        set
        {
            if (_serialNo == value) return;
            _serialNo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialNo)));
        }
    }

    private string _positionX = PositionX;

    /// <summary>芯片坐标 X（晶圆图坐标）。手动录入，允许留空；导出性能测试报表时写入 Position x 列。</summary>
    public string PositionX
    {
        get => _positionX;
        set
        {
            if (_positionX == value) return;
            _positionX = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PositionX)));
        }
    }

    private string _positionY = PositionY;

    /// <summary>芯片坐标 Y（晶圆图坐标）。手动录入，允许留空；导出性能测试报表时写入 Position y 列。</summary>
    public string PositionY
    {
        get => _positionY;
        set
        {
            if (_positionY == value) return;
            _positionY = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PositionY)));
        }
    }

    private bool _isNext;

    /// <summary>UI-only: 当前扫码目标行（待录入）。不参与 CSV 读写。</summary>
    public bool IsNext
    {
        get => _isNext;
        set
        {
            if (_isNext == value) return;
            _isNext = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNext)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Reads `工位对应表.csv`. Header in Chinese:
///     工位,序列号,阀位,板卡位,板卡工位号,层数,夹具位,夹具工位号,压力控制器,数字万用表,通道,阀门,坐标X,坐标Y
/// 坐标X/坐标Y 为后加列，老文件缺列时按空值处理。
/// 编码经 SmartText 兼容历史 GBK/ANSI 与 UTF-8（含 BOM）文件；保存统一 UTF-8 with BOM。
/// </summary>
public sealed class SlotTable
{
    /// <summary>工位对应表表头（列序与 <see cref="SlotEntry"/> 一致，勿随意调整）。</summary>
    public const string CsvHeader = "工位,序列号,阀位,板卡位,板卡工位号,层数,夹具位,夹具工位号,压力控制器,数字万用表,通道,阀门,坐标X,坐标Y";

    public IReadOnlyList<SlotEntry> Entries { get; }

    public SlotTable(IReadOnlyList<SlotEntry> entries) => Entries = entries;

    public static SlotTable Load(string path)
    {
        if (!File.Exists(path)) return new SlotTable(Array.Empty<SlotEntry>());
        var list = new List<SlotEntry>();
        var lines = Common.SmartText.ReadAllLines(path);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = line.Split(',');
            string F(int i) => i < c.Length ? c[i].Trim() : "";
            list.Add(new SlotEntry(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13)));
        }
        return new SlotTable(list);
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine(CsvHeader);
        foreach (var s in Entries)
            sb.AppendLine(FormatRow(s));
        File.WriteAllText(path, sb.ToString(), Common.SmartText.WriteEncoding);
    }

    /// <summary>按表头列序拼一行工位数据，供各导出入口复用。</summary>
    public static string FormatRow(SlotEntry s) =>
        string.Join(',', s.Slot, s.SerialNo, s.Valve, s.Board, s.BoardSlotNo,
            s.Layer, s.Fixture, s.FixtureSlotNo, s.PressureController, s.Dmm, s.Channel, s.ValveAddr,
            s.PositionX, s.PositionY);
}
