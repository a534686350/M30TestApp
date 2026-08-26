using System;
using System.Collections.Generic;
using System.Text;

namespace M30TestApp.Wpf.Mvvm;

/// <summary>
/// 高频日志行缓冲（线程安全）。
///
/// 入队侧：任意线程调用 <see cref="Post"/>，首批触发一次 Dispatcher 合批；
/// 消费侧（UI 线程）：可见窗口用 StringBuilder 增量拼接，溢出时按块裁剪最旧行，
/// 避免历史实现"每 flush 全量 string.Join(2000 行) + 逐条 Add/RemoveAt(0)"的开销。
///
/// UI 绑定 <see cref="Text"/>（OneWay）；滚动由 <see cref="Flushed"/> 事件驱动
/// （每次合批只通知一次，替代逐行 CollectionChanged）。
/// </summary>
public sealed class UiLogBuffer : ViewModelBase
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<int> _visibleLineChars = new(); // 可见窗口内每行的字符数（含换行符）
    private readonly StringBuilder _visible = new();
    private bool _flushPending;
    private int _count;

    public int MaxLines { get; }
    /// <summary>溢出时一次裁掉的行数下限，避免逐行抖动。</summary>
    public int TrimChunk { get; }

    /// <summary>flush 完成且内容可能变化后触发（UI 线程）。View 据此自动滚动到底部。</summary>
    public event EventHandler? Flushed;

    private string _text = "";
    /// <summary>当前可见窗口文本，绑定到 TextBox.Text。</summary>
    public string Text { get => _text; private set => SetField(ref _text, value); }

    public UiLogBuffer(int maxLines, int trimChunk = 200)
    {
        MaxLines = maxLines;
        TrimChunk = trimChunk;
    }

    /// <summary>入队一行（任意线程）。首条排队时调度一次合批刷新。</summary>
    public void Post(string line)
    {
        lock (_gate)
        {
            _pending.Enqueue(line);
            if (_flushPending) return;
            _flushPending = true;
        }
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(Flush));
    }

    /// <summary>清空（须在 UI 线程调用，命令回调满足）。</summary>
    public void Clear()
    {
        lock (_gate) _pending.Clear();
        _visible.Clear();
        _visibleLineChars.Clear();
        _count = 0;
        Text = "";
    }

    private void Flush()
    {
        List<string> batch;
        lock (_gate)
        {
            batch = new List<string>(_pending);
            _pending.Clear();
            _flushPending = false;
        }
        if (batch.Count == 0) return;

        foreach (var line in batch)
        {
            var sepLen = _count > 0 ? 1 : 0;
            if (sepLen > 0) _visible.Append('\n');
            _visible.Append(line);
            _visibleLineChars.Enqueue(sepLen + line.Length);
            _count++;
        }

        if (_count > MaxLines)
        {
            // 至少裁掉 TrimChunk 行并回到上限以内；StringBuilder.Remove 从头部移除是 O(移除长度)
            var remove = Math.Min(_count, Math.Max(TrimChunk, _count - MaxLines));
            while (remove-- > 0 && _visibleLineChars.Count > 0)
            {
                _visible.Remove(0, _visibleLineChars.Dequeue());
                _count--;
            }
        }

        Text = _visible.ToString();
        Flushed?.Invoke(this, EventArgs.Empty);
    }
}
