using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class QueuePanel
{
    private sealed record QueueDrag(MainViewModel Owner, QueueEntry Entry);
    private Point _dragStart;
    private QueueEntry? _dragCandidate;
    private long _lastDragScroll;

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(QueueList);
        _dragCandidate = e.ClickCount == 1 && e.OriginalSource is DependencyObject source &&
                         ItemsControl.ContainerFromElement(QueueList, source) is ListBoxItem
                         { DataContext: QueueEntry { Kind: QueueEntryKind.Upcoming } entry }
            ? entry : null;
    }

    private void QueueList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            return;
        }
        if (_dragCandidate is not { } entry || DataContext is not MainViewModel vm) return;
        var position = e.GetPosition(QueueList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        if (!vm.QueueTimeline.Contains(entry)) return;
        e.Handled = true;
        try
        {
            DragDrop.DoDragDrop(QueueList, new DataObject(typeof(QueueDrag), new QueueDrag(vm, entry)), DragDropEffects.Move);
        }
        finally { DropIndicator.Visibility = Visibility.Collapsed; }
    }

    private QueueDrag? GetQueueDrag(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(QueueDrag)) && e.Data.GetData(typeof(QueueDrag)) is QueueDrag drag &&
        ReferenceEquals(DataContext, drag.Owner) && drag.Entry.Kind == QueueEntryKind.Upcoming &&
        drag.Owner.QueueTimeline.Contains(drag.Entry) && (e.AllowedEffects & DragDropEffects.Move) != 0
            ? drag : null;

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        DropIndicator.Visibility = Visibility.Collapsed;
        if (GetQueueDrag(e) is null) return;
        var position = e.GetPosition(QueueList);
        ScrollDuringDrag(position);
        if (GetDropLocation(position) is not { } location) return;
        e.Effects = DragDropEffects.Move;
        DropIndicatorPosition.Y = Math.Clamp(location.Y, 0, Math.Max(0, QueueList.ActualHeight - 2));
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void QueueList_DragLeave(object sender, DragEventArgs e) => DropIndicator.Visibility = Visibility.Collapsed;

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        DropIndicator.Visibility = Visibility.Collapsed;
        // Resolve against the live queue: playback may have advanced during the drag.
        if (GetQueueDrag(e) is not { } drag || GetDropLocation(e.GetPosition(QueueList)) is not { } location) return;
        drag.Owner.MoveQueueEntry(drag.Entry, location.Index);
        QueueList.ScrollIntoView(drag.Entry);
        e.Effects = DragDropEffects.Move;
    }

    private (int Index, double Y)? GetDropLocation(Point position)
    {
        if (DataContext is not MainViewModel vm || position.X < 0 || position.X >= QueueList.ActualWidth ||
            position.Y < 0 || position.Y >= QueueList.ActualHeight) return null;
        var hit = VisualTreeHelper.HitTest(QueueList, position)?.VisualHit;
        for (var node = hit; node is not null && node != QueueList; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollBar) return null;

        if (hit is not null && ItemsControl.ContainerFromElement(QueueList, hit) is ListBoxItem row &&
            row.DataContext is QueueEntry entry)
        {
            var bounds = row.TransformToAncestor(QueueList).TransformBounds(new Rect(row.RenderSize));
            var after = position.Y >= bounds.Top + bounds.Height / 2;
            if (entry.IsCurrent && after) return (0, bounds.Bottom);
            if (entry.Kind != QueueEntryKind.Upcoming) return null;
            var index = vm.QueueTimeline.IndexOf(entry) - (vm.QueueTimeline.Count - vm.Queue.Count);
            return (index + (after ? 1 : 0), after ? bounds.Bottom : bounds.Top);
        }

        // Empty space below the last row appends; gaps above/within history are not drop targets.
        if (QueueList.ItemContainerGenerator.ContainerFromIndex(QueueList.Items.Count - 1) is ListBoxItem last)
        {
            var bounds = last.TransformToAncestor(QueueList).TransformBounds(new Rect(last.RenderSize));
            if (position.Y >= bounds.Bottom) return (vm.Queue.Count, bounds.Bottom);
        }
        return null;
    }

    private void ScrollDuringDrag(Point position)
    {
        var now = Environment.TickCount64;
        if (now - _lastDragScroll < 100 || (position.Y >= 28 && position.Y <= QueueList.ActualHeight - 28)) return;
        if (FindScrollViewer(QueueList) is not { } scroll) return;
        _lastDragScroll = now;
        if (position.Y < 28) scroll.LineUp();
        else scroll.LineDown();
        QueueList.UpdateLayout();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
