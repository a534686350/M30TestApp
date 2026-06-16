using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// DataGrid with dynamic columns. Columns are observed via <see cref="MatrixColumns"/>.
/// The first two columns (Slot, SerialNo) are static and frozen.
/// </summary>
public partial class DataMatrixGrid : UserControl
{
    public DataMatrixGrid()
    {
        InitializeComponent();
        BuildStaticColumns();
    }

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

    public static readonly DependencyProperty MatrixColumnsProperty = DependencyProperty.Register(
        nameof(MatrixColumns), typeof(IEnumerable), typeof(DataMatrixGrid),
        new PropertyMetadata(null, OnMatrixColumnsChanged));

    public IEnumerable? MatrixColumns
    {
        get => (IEnumerable?)GetValue(MatrixColumnsProperty);
        set => SetValue(MatrixColumnsProperty, value);
    }

    private static void OnMatrixColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataMatrixGrid g) return;
        if (e.OldValue is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= g.OnMatrixColumnsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += g.OnMatrixColumnsCollectionChanged;
        g.RebuildColumns();
    }

    private void OnMatrixColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (MatrixColumnVm col in e.NewItems)
                AddDynamicColumn(col.Key, col.Header);
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
        if (MatrixColumns is null) return;
        foreach (var item in MatrixColumns)
        {
            if (item is MatrixColumnVm col)
                AddDynamicColumn(col.Key, col.Header);
        }
    }

    private void AddDynamicColumn(string columnKey, string header)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            MinWidth = 88,
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
