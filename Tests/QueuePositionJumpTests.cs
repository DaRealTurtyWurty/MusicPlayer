using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckQueuePositionJumps()
    {
        foreach (var repeat in new[] { PlaybackRepeatMode.Off, PlaybackRepeatMode.All })
        foreach (var shuffle in new[] { PlaybackShuffleMode.Off, PlaybackShuffleMode.Tracks, PlaybackShuffleMode.ReshuffleEachLoop })
        {
            var player = new FakePlayer();
            using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
                new Random(42), monitorLibrary: false);
            vm.ShuffleMode = shuffle;
            vm.RepeatMode = repeat;
            var a = Track("A"); var b = Track("B"); var c = Track("C"); var d = Track("D");
            vm.SelectedTrack = a;
            vm.PlaySelectedTrackCommand.Execute(null);
            foreach (var track in new[] { b, a, c, d }) vm.Queue.Add(track);
            var original = vm.QueueTimeline.Select(e => e.Track).ToArray();
            vm.SelectedQueueEntry = vm.QueueTimeline[3];
            var plays = player.PlayCount;
            vm.PlayQueueEntryCommand.Execute(null);
            Check(vm.CurrentTrack == c && player.PlayCount == plays + 1 && vm.Queue[0] == d,
                $"Jump loads only the selected song and continues after it ({repeat}, {shuffle})");
            Check(vm.QueueTimeline.Take(5).Select(e => e.Track).SequenceEqual(original) &&
                vm.QueueTimeline.Take(3).All(e => e.IsHistory) && vm.QueueTimeline[3].IsCurrent,
                "Jump changes the playback position while preserving timeline order and duplicate occurrences");
            Check(vm.Queue.SequenceEqual(repeat == PlaybackRepeatMode.All ? new[] { d, a, b, a } : new[] { d }),
                "Repeat All retains the skipped portion once for the next pass");
            vm.SelectedQueueEntry = vm.QueueTimeline[2];
            vm.PlayQueueEntryCommand.Execute(null);
            Check(vm.CurrentTrack == a && vm.Queue.Take(2).SequenceEqual(new[] { c, d }) &&
                vm.QueueTimeline.Take(5).Select(e => e.Track).SequenceEqual(original),
                "Backward jump targets the selected duplicate occurrence and restores its following songs");
            vm.SelectedQueueEntry = vm.QueueTimeline[0];
            vm.PlayQueueEntryCommand.Execute(null);
            Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, a, c, d }) &&
                vm.QueueTimeline.Select(e => e.Track).SequenceEqual(original),
                "Jumping to the first row restores the original sequence without repeat copies");
            vm.NextCommand.Execute(null);
            Check(vm.CurrentTrack == b, "Next follows the row immediately after a backward jump");
        }
        using (var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(), monitorLibrary: false))
        {
            vm.SelectedTrack = Track("First"); vm.PlaySelectedTrackCommand.Execute(null);
            vm.Queue.Add(Track("Skip")); vm.Queue.Add(Track("Clicked")); vm.Queue.Add(Track("Next"));
            var panel = new MusicPlayer.Views.QueuePanel { DataContext = vm };
            panel.Measure(new System.Windows.Size(296, 560));
            panel.Arrange(new System.Windows.Rect(0, 0, 296, 560));
            panel.UpdateLayout();
            var list = (System.Windows.Controls.ListBox)panel.FindName("QueueList");
            var row = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            vm.SelectedQueueEntry = vm.QueueTimeline[1];
            var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = row
            };
            typeof(MusicPlayer.Views.QueuePanel).GetMethod("QueueList_MouseDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(panel, new object[] { list, args });
            Check(vm.CurrentTrack?.Title == "Clicked" && vm.Queue.Single().Title == "Next" && args.Handled,
                "Double-click jumps to the clicked row even when another row was previously selected");
            panel.DataContext = null;
        }
        using var directory = new TemporaryTestDirectory("QueuePositionJumps");
        var store = new SqliteMusicStore(Path.Combine(directory.Path, "session.db"));
        var fake = new FakePlayer();
        using (var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), fake,
            playbackSessionStore: store, monitorLibrary: false))
        {
            vm.SelectedTrack = Track("Current");
            vm.PlaySelectedTrackCommand.Execute(null);
            vm.Queue.Add(Track("Skipped")); vm.Queue.Add(Track("broken")); vm.Queue.Add(Track("Following"));
            vm.SelectedQueueIndex = 1;
            vm.PlayQueuedTrackCommand.Execute(null);
            Check(vm.CurrentTrack?.Title == "Following" && vm.IsQueueEmpty,
                "Unreadable jump target falls through to the following song, without replaying skipped rows");
        }
        using var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            playbackSessionStore: store, monitorLibrary: false);
        Check(restored.CurrentTrack?.Title == "Following" &&
            restored.QueueTimeline.Where(e => e.IsHistory).Select(e => e.Track.Title).SequenceEqual(new[] { "Current", "Skipped" }),
            "Jump position and preceding rows survive a session restart");
    }
    private static async Task CheckQueueJumpAudioAsync()
    {
        foreach (var crossfade in new[] { false, true })
        {
            var backend = new TestAudioBackend();
            using var player = new NAudioPlayer(backend);
            using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, monitorLibrary: false);
            vm.IsCrossfadeEnabled = crossfade;
            vm.CrossfadeDurationSeconds = 1;
            vm.RepeatMode = PlaybackRepeatMode.All;
            var tracks = Enumerable.Range(0, 5).Select(i => new Track
            {
                FilePath = AudioFixture("wav"), Title = $"Track {i}", ArtworkData = []
            }).ToArray();
            foreach (var track in tracks) vm.Queue.Add(track);
            vm.PlayCommand.Execute(null);
            var oldOutput = backend.Last!;
            oldOutput.ReadAhead(TimeSpan.FromSeconds(2.05));
            vm.SelectedQueueEntry = vm.QueueTimeline[3];
            vm.PlayQueueEntryCommand.Execute(null);
            Check(vm.CurrentTrack == tracks[3] && vm.Queue[0] == tracks[4] && oldOutput.Disposed && vm.IsPlaying,
                $"{(crossfade ? "Crossfade" : "Gapless")} queue jump flushes buffered earlier songs and loads the selected occurrence");
            var output = backend.Last!;
            output.ReadAhead(TimeSpan.FromSeconds(2.05));
            output.RenderedPosition = TimeSpan.FromSeconds(2.05);
            await Task.Delay(60);
            Check(vm.CurrentTrack == tracks[4], "Audio after a jump follows the next queue position");
            vm.SelectedQueueEntry = vm.QueueTimeline[1];
            vm.PlayQueueEntryCommand.Execute(null);
            Check(vm.CurrentTrack == tracks[1] && vm.Queue.Take(3).SequenceEqual(tracks.Skip(2)) && vm.Queue.Count == 4,
                "Backward audio jump restores following positions without duplicating the repeat cycle");
        }
    }

}
