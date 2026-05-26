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
            RaiseItem(key);
        }
    }

    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseItem(TKey key)
    {
        if (PropertyChanged is null) return;
        var indexer = $"Item[{key}]";
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(indexer));
            }));
        else
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(indexer));
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
