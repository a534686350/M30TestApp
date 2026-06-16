using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// Long-term stability matrix: merged temperature header row + sub-column headers.
/// </summary>
public partial class LongTermMatrixGrid : UserControl
{
    private static readonly Brush GroupBrushA = new SolidColorBrush(Color.FromRgb(0xE8, 0xF4, 0xFC));
    private static readonly Brush GroupBrushB = new SolidColorBrush(Color.FromRgb(0xF3, 0xE8, 0xFC));

    private readonly List<MergedTempHeaderCell> _mergedCells = new();
    private ScrollViewer? _gridScrollViewer;
    private bool _syncingScroll;
    private bool _layoutPending;

    public LongTermMatrixGrid()
    {
        InitializeComponent();
        BuildStaticColumns();
        DataGridScrollHelper.EnableDragAndWheelScroll(Grid);
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable), typeof(LongTermMatrixGrid),
        new PropertyMetadata(null, OnRowsChanged));

    public IEnumerable? Rows
    {
        get => (IEnumerable?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LongTermMatrixGrid g) g.Grid.ItemsSource = e.NewValue as IEnumerable;
    }

    public static readonly DependencyProperty LongTermColumnsProperty = DependencyProperty.Register(
        nameof(LongTermColumns), typeof(IEnumerable), typeof(LongTermMatrixGrid),
        new PropertyMetadata(null, OnLongTermColumnsChanged));

    public IEnumerable? LongTermColumns
    {
        get => (IEnumerable?)GetValue(LongTermColumnsProperty);
        set => SetValue(LongTermColumnsProperty, value);
    }

    private static void OnLongTermColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LongTermMatrixGrid g) return;
        if (e.OldValue is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= g.OnColumnsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += g.OnColumnsCollectionChanged;
        g.RebuildColumns();
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _gridScrollViewer = FindScrollViewer(Grid);
        if (_gridScrollViewer is not null)
            _gridScrollViewer.ScrollChanged += OnGridScrollChanged;
    }

    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || e.HorizontalChange == 0) return;
        _syncingScroll = true;
        TempHeaderScroll.ScrollToHorizontalOffset(_gridScrollViewer!.HorizontalOffset);
        _syncingScroll = false;
    }

    private void OnGridLayoutUpdated(object? sender, EventArgs e)
    {
        if (_layoutPending || _mergedCells.Count == 0) return;
        _layoutPending = true;
        Dispatcher.BeginInvoke(SyncMergedHeaderLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SyncMergedHeaderLayout()
    {
        _layoutPending = false;
        if (_mergedCells.Count == 0) return;

        var frozenWidth = Grid.RowHeaderWidth;
        for (var i = 0; i < 2 && i < Grid.Columns.Count; i++)
            frozenWidth += Grid.Columns[i].ActualWidth;
        FrozenHeaderCol.Width = new GridLength(frozenWidth);

        var colIndex = 2;
        foreach (var cell in _mergedCells)
        {
            var width = 0.0;
            for (var i = 0; i < cell.ColumnCount && colIndex < Grid.Columns.Count; i++, colIndex++)
                width += Grid.Columns[colIndex].ActualWidth;
            cell.Border.Width = Math.Max(width, cell.ColumnCount * 76);
        }
    }

    private void BuildStaticColumns()
    {
        Grid.Columns.Clear();
        Grid.Columns.Add(CreateTextColumn("Slot", "工位", 70));
        Grid.Columns.Add(CreateTextColumn("SerialNo", "序列号", 150));
    }

    private void RebuildColumns()
    {
        BuildStaticColumns();
        RebuildMergedTempHeaders();

        if (LongTermColumns is null) return;
        foreach (var item in LongTermColumns)
        {
            if (item is LongTermMatrixColumnVm col)
                AddDataColumn(col);
        }

        RequestLayoutSync();
    }

    private void RebuildMergedTempHeaders()
    {
        MergedTempPanel.Children.Clear();
        _mergedCells.Clear();

        if (LongTermColumns is null) return;

        var columns = LongTermColumns.OfType<LongTermMatrixColumnVm>().ToList();
        if (columns.Count == 0) return;

        var groupStart = 0;
        for (var i = 1; i <= columns.Count; i++)
        {
            if (i < columns.Count && columns[i].TempGroupIndex == columns[groupStart].TempGroupIndex)
                continue;

            var group = columns[groupStart..i];
            var groupIndex = group[0].TempGroupIndex;
            var bg = groupIndex % 2 == 0 ? GroupBrushA : GroupBrushB;
            var border = new Border
            {
                Background = bg,
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                MinWidth = group.Count * 76,
                Child = new TextBlock
                {
                    Text = group[0].TempLabel,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 5, 4, 5),
                },
            };
            MergedTempPanel.Children.Add(border);
            _mergedCells.Add(new MergedTempHeaderCell(border, group.Count));
            groupStart = i;
        }
    }

    private void AddDataColumn(LongTermMatrixColumnVm col)
    {
        var binding = new Binding($"Cells[{col.Key}].Value")
        {
            Mode = BindingMode.OneWay,
            FallbackValue = "",
            TargetNullValue = "",
        };
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = CreateSubHeader(col),
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            MinWidth = 76,
            Binding = binding,
        });
    }

    private DataGridTextColumn CreateTextColumn(string path, string header, double width) =>
        new()
        {
            Header = CreateSubHeader(header),
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            Width = width,
        };

    private static object CreateSubHeader(LongTermMatrixColumnVm col) =>
        CreateSubHeader(col.SubLabel);

    private static object CreateSubHeader(string label) =>
        new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 10,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 2),
            ToolTip = label,
        };

    private void RequestLayoutSync() => Dispatcher.BeginInvoke(
        SyncMergedHeaderLayout, System.Windows.Threading.DispatcherPriority.Loaded);

    private static ScrollViewer? FindScrollViewer(DependencyObject root) =>
        DataGridScrollHelper.FindScrollViewer(root);

    private sealed record MergedTempHeaderCell(Border Border, int ColumnCount);
}
