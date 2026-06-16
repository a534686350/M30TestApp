using M30TestApp.Core.Data;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// Long-term stability matrix column with grouped temperature header.
/// </summary>
public sealed class LongTermMatrixColumnVm
{
    public LongTermMatrixColumnVm(string key, string tempLabel, string subLabel, int tempGroupIndex, LongTermColumnKind kind)
    {
        Key = key;
        TempLabel = tempLabel;
        SubLabel = subLabel;
        TempGroupIndex = tempGroupIndex;
        Kind = kind;
    }

    public string Key { get; }
    public string TempLabel { get; }
    public string SubLabel { get; }
    public int TempGroupIndex { get; }
    public LongTermColumnKind Kind { get; }
    public string FlatHeader => $"{TempLabel}/{SubLabel}";

    public static LongTermMatrixColumnVm From(LongTermMatrixColumn column, int tempGroupIndex) =>
        new(column.Key, column.TempLabel, column.SubLabel, tempGroupIndex, column.Kind);
}
