using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace M30TestApp.Wpf.Views;

internal static class DataGridScrollHelper
{
    private const double DragThreshold = 4;

    /// <summary>Scroll to a row; optionally align it near the bottom of the viewport (for barcode scan follow).</summary>
    public static void ScrollToRow(DataGrid grid, int index, bool alignBottom = false)
    {
        if (index < 0 || index >= grid.Items.Count) return;

        void DoScroll()
        {
            grid.UpdateLayout();
            var item = grid.Items[index];
            grid.ScrollIntoView(item);

            if (!alignBottom) return;

            var row = grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            var scrollViewer = FindScrollViewer(grid);
            if (row is null || scrollViewer is null) return;

            var rowPos = row.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
            var target = rowPos.Y + row.ActualHeight - scrollViewer.ViewportHeight + 8;
            if (target > scrollViewer.VerticalOffset)
                scrollViewer.ScrollToVerticalOffset(Math.Min(target, scrollViewer.ScrollableHeight));
        }

        if (grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow)
            DoScroll();
        else
            grid.Dispatcher.BeginInvoke(DoScroll, DispatcherPriority.Loaded);
    }

    /// <summary>Enable Shift+wheel horizontal scroll and left-button drag pan on the grid's ScrollViewer.</summary>
    public static void EnableDragAndWheelScroll(DataGrid grid)
    {
        if (IsEnabled(grid)) return;
        SetEnabled(grid, true);

        grid.Loaded += (_, _) =>
        {
            var scrollViewer = FindScrollViewer(grid);
            if (scrollViewer is null) return;
            if (IsAttached(scrollViewer)) return;
            SetAttached(scrollViewer, true);

            Point? dragStart = null;
            double startHOffset = 0;
            double startVOffset = 0;
            bool isDragging = false;

            scrollViewer.PreviewMouseWheel += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Shift || scrollViewer.ScrollableWidth <= 0) return;
                var next = scrollViewer.HorizontalOffset - e.Delta;
                scrollViewer.ScrollToHorizontalOffset(Math.Clamp(next, 0, scrollViewer.ScrollableWidth));
                e.Handled = true;
            };

            scrollViewer.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsInsideScrollBar(e.OriginalSource as DependencyObject) ||
                    IsInsideInteractiveEditor(e.OriginalSource as DependencyObject))
                    return;

                dragStart = e.GetPosition(scrollViewer);
                startHOffset = scrollViewer.HorizontalOffset;
                startVOffset = scrollViewer.VerticalOffset;
                isDragging = false;
                scrollViewer.CaptureMouse();
            };

            scrollViewer.PreviewMouseMove += (_, e) =>
            {
                if (dragStart is null || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(scrollViewer);
                var delta = pos - dragStart.Value;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

                isDragging = true;
                scrollViewer.ScrollToHorizontalOffset(Math.Clamp(
                    startHOffset - delta.X, 0, scrollViewer.ScrollableWidth));
                scrollViewer.ScrollToVerticalOffset(Math.Clamp(
                    startVOffset - delta.Y, 0, scrollViewer.ScrollableHeight));
                e.Handled = true;
            };

            void EndDrag(object sender, MouseEventArgs e)
            {
                if (dragStart is null) return;
                if (isDragging) e.Handled = true;
                dragStart = null;
                isDragging = false;
                scrollViewer.ReleaseMouseCapture();
            }

            scrollViewer.PreviewMouseLeftButtonUp += EndDrag;
            scrollViewer.LostMouseCapture += EndDrag;
        };
    }

    /// <summary>Recursively enable drag/wheel scrolling for every DataGrid under <paramref name="root"/>.</summary>
    public static void EnableDragAndWheelScroll(DependencyObject root)
    {
        if (root is null) return;

        if (root is DataGrid grid)
            EnableDragAndWheelScroll(grid);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            EnableDragAndWheelScroll(VisualTreeHelper.GetChild(root, i));
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        ScrollViewer? found = null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) found = sv;
            var nested = FindScrollViewer(child);
            if (nested is not null) found = nested;
        }
        return found;
    }

    private static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(DataGridScrollHelper),
            new PropertyMetadata(false));

    private static bool IsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(EnabledProperty);

    private static void SetEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(EnabledProperty, value);

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached(
            "Attached",
            typeof(bool),
            typeof(DataGridScrollHelper),
            new PropertyMetadata(false));

    private static bool IsAttached(DependencyObject obj) =>
        (bool)obj.GetValue(AttachedProperty);

    private static void SetAttached(DependencyObject obj, bool value) =>
        obj.SetValue(AttachedProperty, value);

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar or Thumb or RepeatButton)
                return true;
        }
        return false;
    }

    private static bool IsInsideInteractiveEditor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase or PasswordBox or ComboBox or ButtonBase or Slider)
                return true;
            if (current is DataGrid)
                return false;
        }
        return false;
    }
}
