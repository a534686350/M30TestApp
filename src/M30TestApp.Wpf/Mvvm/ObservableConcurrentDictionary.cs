using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace M30TestApp.Wpf.Mvvm;

/// <summary>
/// Minimal observable dictionary used by matrix rows so DataGrid cells re-render when
/// updated from background threads. Marshals notifications onto the UI dispatcher.
/// </summary>
public sealed class ObservableConcurrentDictionary<TKey, TValue>
    : IEnumerable<KeyValuePair<TKey, TValue>>, INotifyPropertyChanged
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _inner = new();

    public TValue? this[TKey key]
    {
        get => _inner.TryGetValue(key, out var v) ? v : default;
        set
        {
            _inner[key] = value!;
            RaiseIndexerChanged();
        }
    }

    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);

    /// <summary>
    /// Store a value WITHOUT raising a change notification. Call <see cref="NotifyChanged"/>
    /// afterwards (once per row per batch) to refresh the UI.
    /// </summary>
    public void SetDeferred(TKey key, TValue value) => _inner[key] = value!;

    /// <summary>Raise the indexer change notification so bound cells re-evaluate.</summary>
    public void NotifyChanged() => RaiseIndexerChanged();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raise PropertyChanged with <see cref="Binding.IndexerName"/> (the literal "Item[]").
    ///
    /// 必须用 "Item[]"：WPF 解析绑定路径里的索引器步骤时，会把该步的属性名写成常量
    /// "Item[]"（见 <c>PropertyPath.ResolvePathParts</c>），其变更监听器只对这一个名字生效。
    /// 发 "Item[key]" 这类带键名字 WPF 完全无视——矩阵单元格会停留在首次求值的旧值，
    /// 表现为「只有第一行有数据、其余行永远空白」（v1.2.37 移除 Item[] 后退化）。
    /// 代价是无法按 key 定向刷新，只能整行重估，因此由调用方合批后每行只发一次。
    /// </summary>
    private void RaiseIndexerChanged()
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.BeginInvoke(new System.Action(
                () => handler.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName))));
        else
            handler.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
