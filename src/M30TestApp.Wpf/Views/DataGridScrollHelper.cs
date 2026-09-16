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

    /// <summary>
    /// 把某一行滚动到可见位置，只做**最小滚动**：行已经在视口里就一行都不动，
    /// 避免扫码时视野上下乱跳。alignBottom 为 true 时再多让出一行余量。
    ///
    /// 关键：DataGrid 默认 <c>CanContentScroll=True</c>（按「项」滚动），此时
    /// <c>VerticalOffset</c> / <c>ViewportHeight</c> / <c>ScrollableHeight</c> 的单位都是**项**，
    /// 不是像素。把 <c>TransformToAncestor</c> 得到的像素坐标和它们混着算位移，
    /// 会滚到完全不相干的位置（现场症状：扫码后表格跳到中间某一段）。
    /// </summary>
    public static void ScrollToRow(DataGrid grid, int index, bool alignBottom = false)
    {
        if (index < 0 || index >= grid.Items.Count) return;

        void DoScroll()
        {
            grid.UpdateLayout();

            var scrollViewer = GetGridScrollViewer(grid);
            if (scrollViewer is null)
            {
                grid.ScrollIntoView(grid.Items[index]);
                return;
            }

            // ① 逻辑（按项）滚动：全部用「项」做单位，不碰像素。
            if (scrollViewer.CanContentScroll)
            {
                var first = (int)Math.Round(scrollViewer.VerticalOffset);
                var visible = Math.Max(1, (int)Math.Round(scrollViewer.ViewportHeight));
                var last = first + visible - 1;

                double target;
                if (index < first)
                    target = index;                                     // 在视口上方 → 贴到顶
                else if (index > last)
                    target = index - visible + 1 + (alignBottom ? 1 : 0); // 在视口下方 → 贴到底（可留一行）
                else
                    return;                                             // 已在视口内 → 不动

                scrollViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, scrollViewer.ScrollableHeight));
                return;
            }

            // ② 像素滚动：这里 VerticalOffset / ViewportHeight 才是像素，可放心用坐标差。
            var row = grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            if (row is null || scrollViewer.ViewportHeight <= 0)
            {
                grid.ScrollIntoView(grid.Items[index]);
                return;
            }

            double delta;
            try
            {
                var position = row.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
                var rowTop = position.Y;
                var rowBottom = rowTop + row.ActualHeight;
                var viewport = scrollViewer.ViewportHeight;

                if (rowBottom > viewport)
                    delta = rowBottom - viewport + (alignBottom ? row.ActualHeight : 0);
                else if (rowTop < 0)
                    delta = rowTop - (alignBottom ? row.ActualHeight : 0);
                else
                    return;
            }
            catch (InvalidOperationException)
            {
                grid.ScrollIntoView(grid.Items[index]);
                return;
            }

            scrollViewer.ScrollToVerticalOffset(
                Math.Clamp(scrollViewer.VerticalOffset + delta, 0, scrollViewer.ScrollableHeight));
        }

        // 一律延迟到布局完成后再滚：扫码瞬间行容器/视口尺寸还可能没稳定下来。
        grid.Dispatcher.BeginInvoke(DoScroll, DispatcherPriority.Loaded);
    }

    /// <summary>取 DataGrid 模板里的滚动器（直接视觉子级），避免误取单元格内的嵌套滚动器。</summary>
    private static ScrollViewer? GetGridScrollViewer(DataGrid grid)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(grid); i++)
            if (VisualTreeHelper.GetChild(grid, i) is ScrollViewer viewer)
                return viewer;

        return FindScrollViewer(grid);
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
