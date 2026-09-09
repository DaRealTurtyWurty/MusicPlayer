using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckQueueHistory()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        Check(vm.IsQueueTimelineEmpty, "Timeline starts empty");
        var a = Track("Weightless");
        var b = Track("A Walk");
        var c = Track("First Breath After Coma");
        vm.Tracks.Add(a); vm.Tracks.Add(b); vm.Tracks.Add(c);
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(b); vm.Queue.Add(a); vm.Queue.Add(c);
        player.End();
        Check(vm.QueueTimeline.Select(e => (e.Track, e.Kind)).SequenceEqual(new[]
        {
            (a, QueueEntryKind.History), (b, QueueEntryKind.Current),
            (a, QueueEntryKind.Upcoming), (c, QueueEntryKind.Upcoming)
        }), "Completed and current songs remain in order before upcoming songs");
        vm.SelectedQueueEntry = vm.QueueTimeline[2];
        Check(vm.SelectedQueueIndex == 0 && !vm.MoveQueueUpCommand.CanExecute(null),
            "Upcoming selection uses the correct index after history and cannot move into current playback");
        vm.RemoveFromQueueCommand.Execute(null);
        Check(vm.Queue.Single() == c && vm.QueueTimeline[0].Track == a,
            "Removing an upcoming duplicate retains the played occurrence");
        vm.SelectedQueueEntry = vm.QueueTimeline[0];
        Check(!vm.RemoveFromQueueCommand.CanExecute(null) && !vm.MoveQueueDownCommand.CanExecute(null) &&
              vm.PlayQueueEntryCommand.CanExecute(null), "History can be replayed but is excluded from upcoming edits");
        vm.PlayQueueEntryCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.QueueTimeline.Where(e => e.IsHistory).Select(e => e.Track).SequenceEqual(new[] { a, b }),
            "Replaying history retains the prior playback sequence");
        vm.RepeatMode = PlaybackRepeatMode.One;
        player.End();
        Check(vm.QueueTimeline.Count(e => e.IsHistory) == 2 && vm.QueueTimeline.Count(e => e.IsCurrent) == 1,
            "Repeat one retains a single current occurrence without adding history");
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.QueueTimeline.Select(e => e.Track).SequenceEqual(new[] { a, b, a, c }),
            "Previous restores the forward queue and the matching history display");

        vm.IsQueueOpen = true;
        var window = new MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new Size(884, 561));
            content.Arrange(new Rect(0, 0, 884, 561));
            content.UpdateLayout();
        }
        Layout();
        var list = Descendants(content).OfType<ListBox>().Single(l => l.Name == "QueueList");
        Check(list.Items.Count == 4 && Descendants(list).OfType<TextBlock>().Count(t => t.Text == "Current song") == 1,
            "Queue UI renders history, one labeled current song and upcoming tracks");
        list.SelectedIndex = 3;
        Check(vm.SelectedQueueIndex == 1 && vm.SelectedQueueEntry?.Track == c,
            "Visible row selection maps to the upcoming occurrence");
        vm.MoveQueueUpCommand.Execute(null);
        Layout();
        Check(list.SelectedIndex == 2 && vm.SelectedQueueEntry?.Track == c && vm.Queue[0] == c,
            "Reordering through the queue UI retains selection with history present");

        var output = Path.Combine(AppContext.BaseDirectory, "screenshots", "queue-history.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var bitmap = new RenderTargetBitmap(884, 561, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);

        vm.ClearQueueCommand.Execute(null);
        vm.ConfirmClearQueueCommand.Execute(null);
        Layout();
        Check(list.Items.Count == 2 && !vm.IsQueueTimelineEmpty &&
              Descendants(content).OfType<TextBlock>().Single(t => t.Text == "Queue is empty").Visibility == Visibility.Collapsed,
            "Clear leaves history and current visible without displaying an empty queue message");
        vm.PlayAllCommand.Execute(null);
        Check(vm.QueueTimeline.All(e => !e.IsHistory), "Starting a new playback session resets history");
        var lastPlaying = vm.CurrentTrack;
        vm.Queue.Clear();
        vm.Queue.Add(Track("broken"));
        vm.RepeatMode = PlaybackRepeatMode.Off;
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack is null && vm.QueueTimeline.Single().Track == lastPlaying && vm.QueueTimeline.Single().IsHistory,
            "A failed next track does not discard the previously playing song");
        window.DataContext = null;
        window.Close();
    }
}
