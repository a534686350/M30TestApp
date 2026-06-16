namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// One dynamic matrix column: internal cell key plus display header.
/// </summary>
public sealed class MatrixColumnVm
{
    public MatrixColumnVm(string key, string header)
    {
        Key = key;
        Header = header;
    }

    public string Key { get; }
    public string Header { get; }
}
