using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// DataGrid with dynamic columns. Columns are observed via <see cref="MatrixColumns"/>.
/// The first two columns (Slot, SerialNo) are static and frozen.
///
/// 顶部带一条与 DataGrid 横向滚动同步的 ScrollBar：矩阵列多时，
/// 不用先把纵向滚动条拖到底部才能操作横向滚动。
/// </summary>
public partial class DataMatrixGrid : UserControl
{
    private ScrollViewer? _gridScrollViewer;
    private bool _syncingScroll;

    public DataMatrixGrid()
    {
        InitializeComponent();
        BuildStaticColumns();
        DataGridScrollHelper.EnableDragAndWheelScroll(Grid);
        DataGridScrollHelper.EnableEnterToNextCell(Grid);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
        // 工位 / 序列号不可编辑（它们标识这一行），只读列也会被回车导航自动跳过
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "工位",
            Binding = new Binding("Slot") { Mode = BindingMode.OneWay },
            Width = 70,
            IsReadOnly = true,
        });
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "序列号",
            Binding = new Binding("SerialNo") { Mode = BindingMode.OneWay },
            Width = 150,
            IsReadOnly = true,
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
            // 双向绑到 Cell.EditableValue（不是 Value）：手工编辑要同时刷新 RawValue，
            // 否则界面改了、报表导出的还是采集时的旧原值。
            Binding = new Binding($"Cells[{columnKey}].EditableValue")
            {
                Mode = BindingMode.TwoWay,
                FallbackValue = "",
                TargetNullValue = "",
            },
        };
        Grid.Columns.Add(col);
    }

    // ─── 顶部横向滚动条与 DataGrid 同步 ─────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 模板化后才拿得到 DataGrid 内部的 ScrollViewer
        _gridScrollViewer ??= FindScrollViewer(Grid);
        if (_gridScrollViewer is null) return;

        _gridScrollViewer.ScrollChanged -= OnGridScrollChanged;
        _gridScrollViewer.ScrollChanged += OnGridScrollChanged;
        SyncTopScrollBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_gridScrollViewer is not null)
            _gridScrollViewer.ScrollChanged -= OnGridScrollChanged;
        _gridScrollViewer = null;
    }

    private void OnGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange == 0 && e.ExtentWidthChange == 0 && e.ViewportWidthChange == 0) return;
        SyncTopScrollBar();
    }

    /// <summary>把 DataGrid 的横向滚动状态镜像到顶部滚动条；列少到不需要滚动时自动隐藏。</summary>
    private void SyncTopScrollBar()
    {
        if (_gridScrollViewer is null) return;

        _syncingScroll = true;
        try
        {
            var scrollable = _gridScrollViewer.ScrollableWidth;
            TopScrollBar.Minimum = 0;
            TopScrollBar.Maximum = Math.Max(0, scrollable);
            TopScrollBar.ViewportSize = Math.Max(0, _gridScrollViewer.ViewportWidth);
            TopScrollBar.Value = _gridScrollViewer.HorizontalOffset;
            TopScrollBar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void OnTopScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (_syncingScroll || _gridScrollViewer is null) return;
        _gridScrollViewer.ScrollToHorizontalOffset(e.NewValue);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}
