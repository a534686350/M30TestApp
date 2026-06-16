using System;
using System.Windows;
using System.Windows.Controls;
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
        grid.Loaded += (_, _) =>
        {
            var scrollViewer = FindScrollViewer(grid);
            if (scrollViewer is null) return;

            Point? dragStart = null;
            double startHOffset = 0;
            double startVOffset = 0;

            scrollViewer.PreviewMouseWheel += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Shift || scrollViewer.ScrollableWidth <= 0) return;
                var next = scrollViewer.HorizontalOffset - e.Delta;
                scrollViewer.ScrollToHorizontalOffset(Math.Clamp(next, 0, scrollViewer.ScrollableWidth));
                e.Handled = true;
            };

            scrollViewer.PreviewMouseLeftButtonDown += (_, e) =>
            {
                dragStart = e.GetPosition(scrollViewer);
                startHOffset = scrollViewer.HorizontalOffset;
                startVOffset = scrollViewer.VerticalOffset;
                scrollViewer.CaptureMouse();
            };

            scrollViewer.PreviewMouseMove += (_, e) =>
            {
                if (dragStart is null || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(scrollViewer);
                var delta = pos - dragStart.Value;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

                scrollViewer.ScrollToHorizontalOffset(Math.Clamp(
                    startHOffset - delta.X, 0, scrollViewer.ScrollableWidth));
                scrollViewer.ScrollToVerticalOffset(Math.Clamp(
                    startVOffset - delta.Y, 0, scrollViewer.ScrollableHeight));
                e.Handled = true;
            };

            void EndDrag(object sender, MouseEventArgs e)
            {
                if (dragStart is null) return;
                dragStart = null;
                scrollViewer.ReleaseMouseCapture();
            }

            scrollViewer.PreviewMouseLeftButtonUp += EndDrag;
            scrollViewer.LostMouseCapture += EndDrag;
        };
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
}
