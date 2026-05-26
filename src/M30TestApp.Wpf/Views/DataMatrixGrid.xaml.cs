using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// DataGrid with dynamic columns. Columns are observed via <see cref="Columns"/>
/// (an INotifyCollectionChanged-backed list of strings). The first two columns
/// (Slot, SerialNo) are static and frozen.
///
/// Cells bind via a custom path "Cells[ColumnKey].Value" on each MatrixRowVm.
/// </summary>
public partial class DataMatrixGrid : UserControl
{
    public DataMatrixGrid()
    {
        InitializeComponent();
        BuildStaticColumns();
    }

    // ─── Rows DP ────────────────────────────────────────────────────────────
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable), typeof(DataMatrixGrid),
        new PropertyMetadata(null, OnRowsChanged));

    public IEnumerable? Rows
    {
        get => (IEnumerable?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataMatrixGrid g) g.Grid.ItemsSource = e.NewValue as IEnumerable;
    }

    // ─── Columns DP ─────────────────────────────────────────────────────────
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(IEnumerable), typeof(DataMatrixGrid),
        new PropertyMetadata(null, OnColumnsChanged));

    public IEnumerable? Columns
    {
        get => (IEnumerable?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataMatrixGrid g) return;
        if (e.OldValue is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= g.OnColumnsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += g.OnColumnsCollectionChanged;
        g.RebuildColumns();
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (string col in e.NewItems) AddDynamicColumn(col);
        }
        else
        {
            RebuildColumns();
        }
    }

    private void BuildStaticColumns()
    {
        Grid.Columns.Clear();
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "工位",
            Binding = new Binding("Slot") { Mode = BindingMode.OneWay },
            Width = 70,
        });
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "序列号",
            Binding = new Binding("SerialNo") { Mode = BindingMode.OneWay },
            Width = 150,
        });
    }

    private void RebuildColumns()
    {
        BuildStaticColumns();
        if (Columns is null) return;
        foreach (var c in Columns)
            if (c is string s) AddDynamicColumn(s);
    }

    private void AddDynamicColumn(string columnKey)
    {
        var col = new DataGridTextColumn
        {
            Header = columnKey,
            Width = 90,
            Binding = new Binding($"Cells[{columnKey}].Value")
            {
                Mode = BindingMode.OneWay,
                FallbackValue = "",
                TargetNullValue = "",
            },
        };
        Grid.Columns.Add(col);
    }
}
