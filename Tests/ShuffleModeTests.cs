using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static void CheckShuffleModes()
    {
        using var directory = new TemporaryTestDirectory("ShuffleModes");
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonUiPreferencesStore(path);
        Track Song(string title, string artist, string album, uint disc, uint number) => new()
        {
            FilePath = title, Title = title, Artist = artist, AlbumArtist = artist,
            Album = album, DiscNumber = disc, TrackNumber = number
        };
        var a1 = Song("A1", "A", "First", 1, 1);
        var a2 = Song("A2", "A", "First", 1, 2);
        var a3 = Song("A3", "A", "First", 2, 1);
        var b1 = Song("B1", "B", "Second", 1, 1);
        var b2 = Song("B2", "B", "Second", 1, 2);
        var c = Song("C", "C", "Third", 1, 1);
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            new Random(42), uiPreferencesStore: store, monitorLibrary: false);
        var tracks = new[] { a3, b2, a2, c, a1, b1, a1 };
        foreach (var track in tracks) vm.Queue.Add(track);
        var selected = vm.QueueTimeline[6];
        vm.SelectedQueueEntry = selected;
        vm.ShuffleMode = PlaybackShuffleMode.Albums;
        Check(vm.Queue.Where(t => t.Artist == "A").SequenceEqual(new[] { a1, a1, a2, a3 }), "Album shuffle orders discs/tracks and retains duplicate occurrences");
        Check(vm.SelectedQueueEntry == selected && vm.Queue[vm.SelectedQueueIndex] == a1, "Grouped shuffle preserves the selected duplicate occurrence");
        bool Contiguous(Func<Track, string> key) => vm.Queue.Select(key).Where((k, i) => i == 0 || k != key(vm.Queue[i - 1])).Distinct().Count() ==
            vm.Queue.Select(key).Where((k, i) => i == 0 || k != key(vm.Queue[i - 1])).Count();
        Check(Contiguous(MusicGroup.AlbumKey), "Albums remain contiguous");
        vm.ShuffleMode = PlaybackShuffleMode.Artists;
        Check(Contiguous(MusicGroup.ArtistName), "Artists remain contiguous");
        vm.Queue.Clear();
        foreach (var track in new[] { a1, a2, b1, b2, c }) vm.Queue.Add(track);
        vm.ShuffleMode = PlaybackShuffleMode.Tracks;
        vm.AvoidConsecutiveArtists = true;
        vm.AvoidConsecutiveAlbums = true;
        Check(vm.Queue.Zip(vm.Queue.Skip(1)).All(pair => pair.First.Artist != pair.Second.Artist &&
            MusicGroup.AlbumKey(pair.First) != MusicGroup.AlbumKey(pair.Second)), "Rules avoid artist and album repetition when feasible");
        vm.Queue.Clear();
        vm.Queue.Add(a1); vm.Queue.Add(a2);
        vm.AvoidConsecutiveArtists = false;
        vm.AvoidConsecutiveArtists = true;
        Check(vm.Queue.Count == 2, "Impossible rules retain all songs and terminate");
        for (var seed = 0; seed < 20; seed++)
        {
            using var variety = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                new Random(seed), monitorLibrary: false);
            foreach (var artist in new[] { "A", "B", "C", "D" })
                for (var i = 0; i < 15; i++) variety.Queue.Add(Song($"{artist}{i}", artist, artist, 1, (uint)(i + 1)));
            variety.AvoidConsecutiveArtists = true;
            variety.AvoidConsecutiveAlbums = true;
            variety.ShuffleMode = PlaybackShuffleMode.Tracks;
            var order = variety.Queue.Select(t => t.Artist).ToArray();
            Check(order.Zip(order.Skip(1)).All(pair => pair.First != pair.Second),
                $"Artist variety preserves the consecutive-repeat rule (seed {seed})");
            Check(Enumerable.Range(2, 36).All(i => order[i] != order[i - 2]),
                $"Artist cooldown prevents two-artist ping-pong while the queue has variety (seed {seed})");
            Check(variety.Queue.Count == 60 && variety.Queue.Distinct().Count() == 60,
                "Artist cooldown keeps every queued occurrence");
        }
        using (var boundary = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            new Random(42), monitorLibrary: false))
        {
            boundary.SelectedTrack = b1; boundary.PlaySelectedTrackCommand.Execute(null);
            boundary.SelectedTrack = a1; boundary.PlaySelectedTrackCommand.Execute(null);
            foreach (var song in new[] { b1, b2, c, c }) boundary.Queue.Add(song);
            boundary.AvoidConsecutiveArtists = true;
            boundary.ShuffleMode = PlaybackShuffleMode.Tracks;
            Check(boundary.Queue[0] == c, "Cooldown includes current playback and history when reshuffling");
        }
        var settings = new SettingsView { DataContext = vm };
        var settingsWindow = new System.Windows.Window { Content = settings, Width = 800, Height = 760, ShowInTaskbar = false };
        settingsWindow.Show();
        settingsWindow.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Check(settings.ShuffleModeSelector.SelectedItem is PlaybackShuffleMode.Tracks, "Settings selector reflects the active shuffle mode");
        settingsWindow.Close();
        foreach (var mode in Enum.GetValues<PlaybackShuffleMode>())
        {
            vm.SetShuffleModeCommand.Execute(mode);
            vm.RepeatMode = PlaybackRepeatMode.One;
            vm.Volume = 32;
            using var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                uiPreferencesStore: new JsonUiPreferencesStore(path), monitorLibrary: false);
            Check(restored.ShuffleMode == mode && restored.AvoidConsecutiveArtists && restored.AvoidConsecutiveAlbums &&
                restored.RepeatMode == PlaybackRepeatMode.One, $"Mode {mode} and rules persist across other preference changes");
        }
        File.WriteAllText(path, "{\"IsShuffleEnabled\":true}");
        Check(store.LoadShuffleMode() == PlaybackShuffleMode.Tracks, "Legacy enabled shuffle migrates to Tracks");
        File.WriteAllText(path, "{\"ShuffleMode\":999,\"IsShuffleEnabled\":true}");
        Check(store.LoadShuffleMode() == PlaybackShuffleMode.Off, "Invalid shuffle mode falls back to Off");

        var player = new FakePlayer();
        using var loop = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, new Random(42), monitorLibrary: false);
        foreach (var track in new[] { a1, a2, b1, b2, c }) loop.Tracks.Add(track);
        loop.ShuffleMode = PlaybackShuffleMode.ReshuffleEachLoop;
        loop.RepeatMode = PlaybackRepeatMode.All;
        loop.PlayAllCommand.Execute(null);
        var first = new List<Track>();
        var second = new List<Track>();
        for (var i = 0; i < 10; i++)
        {
            (i < 5 ? first : second).Add(loop.CurrentTrack!);
            var expected = loop.Queue[0];
            player.End();
            Check(loop.CurrentTrack == expected, "Loop reshuffle plays the displayed upcoming song");
        }
        Check(first.Count == 5 && first.Distinct().Count() == 5 && second.Distinct().Count() == 5 &&
            !first.SequenceEqual(second), "Each loop plays every song once in a fresh order");
        loop.RepeatMode = PlaybackRepeatMode.Off;
        var remaining = loop.Queue.Count;
        for (var i = 0; i <= remaining; i++) player.End();
        Check(!loop.IsPlaying && loop.Queue.Count == 0, "Loop shuffle stops without Repeat All");
    }
    private static async Task CheckShuffleAudioLoopsAsync()
    {
        foreach (var crossfade in new[] { false, true })
        {
            var backend = new TestAudioBackend();
            using var player = new NAudioPlayer(backend);
            using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
                new Random(42), monitorLibrary: false);
            vm.IsCrossfadeEnabled = crossfade;
            vm.CrossfadeDurationSeconds = 1;
            for (var i = 0; i < 5; i++) vm.Tracks.Add(new Track
            {
                FilePath = AudioFixture("wav"), Title = $"Song {i}", Artist = $"Artist {i % 3}", Album = $"Album {i}", ArtworkData = []
            });
            vm.ShuffleMode = PlaybackShuffleMode.ReshuffleEachLoop;
            vm.AvoidConsecutiveArtists = true;
            vm.AvoidConsecutiveAlbums = true;
            vm.RepeatMode = PlaybackRepeatMode.All;
            vm.PlayAllCommand.Execute(null);
            var heard = new List<Track>();
            var output = backend.Last!;
            var rendered = TimeSpan.Zero;
            for (var i = 0; i < 15; i++)
            {
                heard.Add(vm.CurrentTrack!);
                var expected = vm.Queue[0];
                var outgoing = vm.CurrentTrack;
                for (var tick = 0; tick < 35 && vm.CurrentTrack == outgoing; tick++)
                {
                    output.ReadAhead(TimeSpan.FromSeconds(.1));
                    rendered += TimeSpan.FromSeconds(.1);
                    output.RenderedPosition = rendered;
                    await Task.Delay(30);
                }
                Check(vm.CurrentTrack == expected && backend.Last == output && vm.Queue.Count == 4,
                    $"{(crossfade ? "Crossfade" : "Gapless")} loop advances to displayed next occurrence on the same output");
            }
            for (var i = 0; i < 3; i++) Check(heard.Skip(i * 5).Take(5).Distinct().Count() == 5,
                "Audio loop plays each occurrence once per pass, including duplicate file paths");
            var count = vm.Queue.Count;
            vm.PreviousCommand.Execute(null);
            Check(vm.Queue.Count == count, "Previous after loop reshuffling preserves repeat cycle size");
        }
    }

}
