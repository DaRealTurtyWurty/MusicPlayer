using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static void CheckQueuePlaybackScrolling()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        vm.SelectedTrack = Track("First song");
        vm.PlaySelectedTrackCommand.Execute(null);
        for (var i = 0; i < 30; i++) vm.Queue.Add(Track($"Queued song {i}"));

        var panel = new QueuePanel { DataContext = vm };
        var window = new Window
        {
            Content = panel, Width = 296, Height = 560,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        window.Show();
        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        Layout();
        var list = (ListBox)panel.FindName("QueueList");
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        Check(scroll.VerticalOffset == 0, "Playback without history starts at the top of the queue");

        void CheckCurrentPosition(string message)
        {
            Layout();
            var currentIndex = vm.QueueTimeline.ToList().FindIndex(entry => entry.IsCurrent);
            var previous = list.ItemContainerGenerator.ContainerFromIndex(currentIndex - 1) as ListBoxItem;
            var current = list.ItemContainerGenerator.ContainerFromIndex(currentIndex) as ListBoxItem;
            Check(previous is not null && current is not null &&
                  Math.Abs(previous.TranslatePoint(new Point(), list).Y) < 1 &&
                  Math.Abs(current.TranslatePoint(new Point(), list).Y - previous.ActualHeight) < 1,
                message);
        }

        for (var i = 0; i < 12; i++)
        {
            player.End();
            CheckCurrentPosition("Advancing playback keeps exactly one previous song above the current song");
        }
        vm.PreviousCommand.Execute(null);
        CheckCurrentPosition("Previous also anchors the current song below one history row");

        scroll.ScrollToTop();
        Layout();
        panel.Visibility = Visibility.Collapsed;
        player.End();
        Layout();
        panel.Visibility = Visibility.Visible;
        CheckCurrentPosition("Reopening the queue restores the current song near the top");

        scroll.ScrollToTop();
        Layout();
        Check(scroll.VerticalOffset == 0, "History remains available through manual scrolling");
        while (vm.Queue.Count > 0)
        {
            player.End();
            CheckCurrentPosition("The current song stays in the second row through the end of the queue");
        }
        window.Height = 720;
        CheckCurrentPosition("Resizing preserves the current song's position at the end of the queue");
        scroll.ScrollToTop();
        Layout();
        panel.Visibility = Visibility.Collapsed;
        Layout();
        panel.Visibility = Visibility.Visible;
        CheckCurrentPosition("Reopening can find the final song from distant, virtualized history");
        panel.DataContext = null;
        window.Close();
    }

    private static void CheckQueueInteractions()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        vm.SelectedTrack = Track("History");
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.SelectedTrack = Track("Current");
        vm.PlaySelectedTrackCommand.Execute(null);
        var a = Track("A"); var b = Track("B"); var c = Track("C");
        vm.Queue.Add(a); vm.Queue.Add(b); vm.Queue.Add(a); vm.Queue.Add(c);
        var duplicate = vm.QueueTimeline[4];
        vm.MoveQueueEntry(duplicate, 0);
        Check(vm.Queue.SequenceEqual(new[] { a, a, b, c }) && vm.QueueTimeline[2] == duplicate &&
              vm.SelectedQueueIndex == 0 && duplicate.IsNext, "Dragging a duplicate to the front moves the exact occurrence and next label");
        vm.MoveQueueEntry(duplicate, vm.Queue.Count);
        Check(vm.Queue.SequenceEqual(new[] { a, b, c, a }) && vm.QueueTimeline[^1] == duplicate &&
              vm.SelectedQueueIndex == 3 && !duplicate.IsNext, "Dragging down to the end accounts for removal of the source row");
        var before = vm.QueueTimeline.ToArray();
        vm.MoveQueueEntry(duplicate, 3);
        vm.MoveQueueEntry(vm.QueueTimeline[0], 1);
        vm.MoveQueueEntry(vm.QueueTimeline[1], 1);
        vm.MoveQueueEntry(duplicate, -1);
        vm.MoveQueueEntry(new QueueEntry(a, QueueEntryKind.Upcoming), 0);
        Check(vm.QueueTimeline.SequenceEqual(before) && player.PlayCount == 2,
            "Adjacent, stale and invalid drops preserve order, history and current playback");

        var panel = new QueuePanel { DataContext = vm };
        void Layout()
        {
            panel.Measure(new Size(296, 560));
            panel.Arrange(new Rect(0, 0, 296, 560));
            panel.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Layout();
        var list = (ListBox)panel.FindName("QueueList");
        Check(list.AllowDrop && Descendants(panel).OfType<Button>().Single().Content.Equals("Clear"),
            "Queue supports drops and removes the per-song action buttons");

        var getLocation = typeof(QueuePanel).GetMethod("GetDropLocation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (int Index, double Y)? Location(Point point) => ((int, double)?)getLocation.Invoke(panel, [point]);
        Rect Bounds(int index)
        {
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            return row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize));
        }
        var firstUpcoming = Bounds(2);
        var lastUpcoming = Bounds(5);
        Check(Location(new Point(20, firstUpcoming.Top + 2))?.Index == 0 &&
              Location(new Point(20, lastUpcoming.Bottom - 2))?.Index == 4 &&
              Location(new Point(20, lastUpcoming.Bottom + 10))?.Index == 4 &&
              Location(new Point(20, Bounds(0).Top + 2)) is null,
            "Drag hit testing resolves before, after and blank-space insertion points while excluding history");

        MenuItem[] OpenMenu(int index)
        {
            Layout();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [row, true], null)!;
            row.RaiseEvent(args);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            return row.ContextMenu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToArray();
        }
        var items = OpenMenu(3);
        Check(vm.SelectedQueueIndex == 1 && items.Take(4).Select(i => i.Header.ToString()).SequenceEqual(new[]
              { "_Play", "_Remove", "Move _up", "Move _down" }) && items.Take(4).All(i => i.Command is not null),
            "Opening a row menu selects the clicked song and binds all four queue commands");
        items[2].Command.Execute(null);
        Check(vm.Queue[0] == b && vm.SelectedQueueIndex == 0, "Context-menu Move up targets the clicked row");
        items = OpenMenu(2);
        Check(!items[2].IsEnabled && items[3].IsEnabled, "Context-menu move actions respect the upcoming boundary");
        items[3].Command.Execute(null);
        Check(vm.Queue[1] == b, "Context-menu Move down restores the clicked row's position");
        items = OpenMenu(5);
        items[1].Command.Execute(null);
        Check(vm.Queue.SequenceEqual(new[] { a, b, c }), "Context-menu Remove deletes only the clicked duplicate");
        items = OpenMenu(0);
        Check(items[0].IsEnabled && items.Skip(1).Take(3).All(i => !i.IsEnabled),
            "History offers replay while its removal and reorder actions remain disabled");
        items = OpenMenu(3);
        items[0].Command.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue.SequenceEqual(new[] { a, c }), "Context-menu Play starts its selected queue occurrence");
        panel.DataContext = null;
    }
}
