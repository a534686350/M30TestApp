using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace M30TestApp.Wpf.Views;

public sealed class PointBatchRuleInput : INotifyPropertyChanged
{
    private string _range = "";
    private string _value = "";
    private string _extra = "";
    private string _countText = "";

    public PointBatchRuleInput()
    {
    }

    public PointBatchRuleInput(string range, string value, string extra)
    {
        _range = range;
        _value = value;
        _extra = extra;
    }

    public string Range
    {
        get => _range;
        set
        {
            if (_range == value) return;
            _range = value;
            OnPropertyChanged(nameof(Range));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged(nameof(Value));
        }
    }

    public string Extra
    {
        get => _extra;
        set
        {
            if (_extra == value) return;
            _extra = value;
            OnPropertyChanged(nameof(Extra));
        }
    }

    public string CountText
    {
        get => _countText;
        private set
        {
            if (_countText == value) return;
            _countText = value;
            OnPropertyChanged(nameof(CountText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsBlank =>
        string.IsNullOrWhiteSpace(Range) &&
        string.IsNullOrWhiteSpace(Value) &&
        string.IsNullOrWhiteSpace(Extra);

    internal PointBatchRuleInput Clone() => new(Range.Trim(), Value.Trim(), Extra.Trim());

    internal void SetCountText(string value) => CountText = value;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class PointBatchEditorWindow : Window, INotifyPropertyChanged
{
    private string _totalCountText = "总点数：0 个";

    public PointBatchEditorWindow(
        string title,
        string valueHeader,
        string extraHeader,
        string hint,
        IEnumerable<PointBatchRuleInput> rows)
    {
        InitializeComponent();
        Title = title;
        ValueColumn.Header = valueHeader;
        ExtraColumn.Header = extraHeader;
        HintTextBlock.Text = hint;

        foreach (var row in rows)
            Rules.Add(row.Clone());
        if (Rules.Count == 0)
            Rules.Add(new PointBatchRuleInput());

        Rules.CollectionChanged += Rules_CollectionChanged;
        foreach (var row in Rules)
            row.PropertyChanged += Row_PropertyChanged;
        DataContext = this;
        RecalculateCounts();
    }

    public ObservableCollection<PointBatchRuleInput> Rules { get; } = new();

    public IReadOnlyList<PointBatchRuleInput> ResultRules { get; private set; } =
        Array.Empty<PointBatchRuleInput>();

    public string TotalCountText
    {
        get => _totalCountText;
        private set
        {
            if (_totalCountText == value) return;
            _totalCountText = value;
            OnPropertyChanged(nameof(TotalCountText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Rules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (PointBatchRuleInput row in e.OldItems)
                row.PropertyChanged -= Row_PropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (PointBatchRuleInput row in e.NewItems)
                row.PropertyChanged += Row_PropertyChanged;
        }

        RecalculateCounts();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RecalculateCounts();

    private void RecalculateCounts()
    {
        var activeRows = Rules.Where(r => !r.IsBlank).ToList();
        var singleInput = activeRows.Count == 1;
        var total = 0;
        var hasError = false;

        foreach (var row in Rules)
        {
            if (row.IsBlank)
            {
                row.SetCountText("");
                continue;
            }

            if (TryCountRange(row.Range, singleInput, out var count))
            {
                total += count;
                row.SetCountText($"{count.ToString(CultureInfo.InvariantCulture)} 个点");
            }
            else
            {
                hasError = true;
                row.SetCountText("范围错误");
            }
        }

        TotalCountText = hasError
            ? $"总点数：{total.ToString(CultureInfo.InvariantCulture)} 个（有范围错误）"
            : $"总点数：{total.ToString(CultureInfo.InvariantCulture)} 个";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var activeRows = Rules.Where(r => !r.IsBlank).ToList();
        if (activeRows.Count == 0)
        {
            MessageBox.Show("请至少录入一行点位。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var singleInput = activeRows.Count == 1;
        var badRow = activeRows.FirstOrDefault(r => !TryCountRange(r.Range, singleInput, out _));
        if (badRow is not null)
        {
            MessageBox.Show($"范围/序号格式不正确：{badRow.Range}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultRules = activeRows.Select(r => r.Clone()).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static bool TryCountRange(string text, bool singleInput, out int count)
    {
        count = 0;
        var normalized = NormalizeRange(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (singleInput &&
            Regex.IsMatch(normalized, @"^\d+$", RegexOptions.IgnoreCase) &&
            int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalCount) &&
            totalCount > 0)
        {
            count = totalCount;
            return true;
        }

        var tokens = normalized
            .Split(new[] { ',', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        foreach (var token in tokens)
        {
            var match = Regex.Match(
                token,
                @"^[PT]?(?<start>\d+)(?:[-~至到][PT]?(?<end>\d+))?$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            var end = match.Groups["end"].Success
                ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)
                : start;
            if (start <= 0 || end <= 0)
                return false;

            count += Math.Abs(end - start) + 1;
        }

        return count > 0;
    }

    private static string NormalizeRange(string text) =>
        Regex.Replace(
                text.Replace('，', ',')
                    .Replace('：', ':')
                    .Trim(),
                @"\s*([-~至到])\s*",
                "$1")
            .Trim();

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
