using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckListeningHistory()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerListeningHistoryTests");
        var path = Path.Combine(temporary.Path, "music.db");
        MusicDbContext Context() => new(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={path}").Options);
        using (var db = Context()) db.GetService<IMigrator>().Migrate("20260930110725_TrackSequenceMetadata");
        var store = new SqliteMusicStore(path);
        var a = new Track { FilePath = Path.Combine(temporary.Path, "a.mp3"), Title = "First song", Artist = "Artist", Duration = TimeSpan.FromMinutes(3), ExplicitlyAddedToLibrary = true };
        var b = new Track { FilePath = Path.Combine(temporary.Path, "b.mp3"), Title = "Second song", Duration = TimeSpan.FromMinutes(3), ExplicitlyAddedToLibrary = true };
        store.SaveLibrary([a, b]);
        Check(store.LoadListeningHistory().Count == 0 && store.LoadLibrary().All(t => t.PlayCount == 0), "Existing databases migrate with empty listening history and zero counts");
        using (var db = Context()) Check(!db.Database.HasPendingModelChanges(), "Listening history migration matches the EF model");
        var player = new FakePlayer();
        using (var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
                   libraryStore: store, playbackSessionStore: store, monitorLibrary: false))
        {
            vm.SelectedTrack = vm.Tracks[0];
            vm.PlaySelectedTrackCommand.Execute(null);
            Check(vm.ListeningHistory.Count == 1 && vm.Tracks[0].PlayCount == 1, "Successful playback immediately updates history and the library count");
            vm.PlayCommand.Execute(null);
            vm.PauseCommand.Execute(null);
            vm.PlayCommand.Execute(null);
            vm.PositionSeconds = 80;
            Check(vm.ListeningHistory.Count == 1, "Play while playing, pause/resume and seeking do not double-count");
            vm.StopCommand.Execute(null);
            vm.PlayCommand.Execute(null);
            Check(vm.ListeningHistory.Count == 2 && vm.CurrentTrack!.PlayCount == 2, "Stop and restart creates a new play");
            vm.RepeatMode = PlaybackRepeatMode.One;
            player.End();
            Check(vm.ListeningHistory.Count == 3 && vm.CurrentTrack!.PlayCount == 3, "Repeat one records a separate play");
            vm.RepeatMode = PlaybackRepeatMode.Off;
            vm.Queue.Add(vm.Tracks[1]);
            player.End();
            Check(vm.ListeningHistory.Count == 4 && vm.ListeningHistory[0].Track.Title == b.Title, "Automatic queue advancement records the next song");
            vm.PlayHistoryEntryCommand.Execute(vm.ListeningHistory.Last());
            Check(vm.ListeningHistory.Count == 5 && vm.CurrentTrack!.PlayCount == 4, "History replay records a new occurrence");
            vm.SelectedTrack = Track("broken");
            vm.PlaySelectedTrackCommand.Execute(null);
            Check(vm.ListeningHistory.Count == 5, "Failed audio loads do not create plays");
            vm.LibrarySort = LibrarySort.Title;
            vm.PlayAllCommand.Execute(null);
            Check(vm.ListeningHistory.Count == 6 && vm.ListeningHistory.Last().Track.Title == a.Title, "New queue sessions preserve lifetime listening history");
            vm.LibrarySort = LibrarySort.PlayCount;
            vm.LibrarySortDescending = true;
            Check(((Track)vm.LibraryTracks.GetItemAt(0)).Title == a.Title, "Library can sort by play count");
            vm.SelectedPage = AppPage.History;
            vm.IsQueueOpen = true;
            var window = new MainWindow(vm);
            var content = (FrameworkElement)window.Content;
            content.SetValue(Panel.BackgroundProperty, window.Background);
            content.Measure(new Size(884, 561));
            content.Arrange(new Rect(0, 0, 884, 561));
            content.UpdateLayout();
            var history = Descendants(content).OfType<MusicPlayer.Views.HistoryView>().Single();
            var list = (ListView)history.FindName("HistoryList");
            Check(list.Items.Count == 6 && Descendants(list).OfType<TextBlock>().Any(t => t.Text == "5 plays"), "History page binds timestamps and live counts with the queue open");
            foreach (var button in Descendants(content).OfType<Button>().Where(t => t.Command == vm.NavigateCommand))
            {
                var point = button.TransformToAncestor(content).Transform(new Point());
                Check(point.X >= 0 && point.X + button.ActualWidth <= 884, "Navigation fits the minimum window width");
            }
            var output = Path.Combine(AppContext.BaseDirectory, "screenshots", "listening-history.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var bitmap = new RenderTargetBitmap(884, 561, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) encoder.Save(stream);
            window.DataContext = null;
            window.Close();
        }
        using (var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                   libraryStore: new SqliteMusicStore(path), playbackSessionStore: new SqliteMusicStore(path), monitorLibrary: false))
        {
            Check(!restored.IsPlaying && restored.ListeningHistory.Count == 6 && restored.Tracks[0].PlayCount == 5, "Restart restores history and counts without counting paused session loading");
        }
        var timestamp = DateTimeOffset.UtcNow.AddHours(-1);
        store.RecordListen(b, timestamp);
        store.RecordListen(b, timestamp);
        var historyRows = new SqliteMusicStore(path).LoadListeningHistory();
        Check(historyRows.Count == 8 && historyRows[^2].Id > historyRows[^1].Id && historyRows[^1].PlayedAt == timestamp,
            "History preserves duplicate listens, UTC timestamps and deterministic ordering");
        Check(store.LoadListeningHistory(2).Count == 2, "Recent-history limits do not remove stored listens");
        store.SaveLibrary([a, b]);
        Check(store.LoadLibrary()[0].PlayCount == 5, "Metadata saves cannot overwrite authoritative counts");
        using (var db = Context()) db.Database.ExecuteSqlRaw("CREATE TRIGGER RejectListen BEFORE INSERT ON Listens BEGIN SELECT RAISE(ABORT, 'Injected listen failure'); END");
        try { store.RecordListen(a, DateTimeOffset.UtcNow); throw new InvalidOperationException("Expected failure"); }
        catch (DbUpdateException) { }
        Check(store.LoadListeningHistory().Count == 8 && store.LoadLibrary()[0].PlayCount == 5, "Failed history inserts roll back play counts atomically");
        using (var db = Context()) db.Database.ExecuteSqlRaw("DROP TRIGGER RejectListen");
        store.RelocateTrack(a.FilePath, b);
        Check(store.LoadLibrary().Single().PlayCount == 8 && store.LoadListeningHistory().All(e => e.Track.FilePath == b.FilePath),
            "Relinking onto an existing song merges both play counts and all history references");
    }
}
