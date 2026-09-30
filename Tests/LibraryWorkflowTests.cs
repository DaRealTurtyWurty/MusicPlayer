using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static void CheckSettingsNavigation()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        var window = new MusicPlayer.MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(884, 521));
        content.Arrange(new Rect(0, 0, 884, 521));
        content.UpdateLayout();
        var navigation = Descendants(content).OfType<Button>().Where(b => b.CommandParameter is AppPage).ToArray();
        Check(navigation.Select(b => (AppPage)b.CommandParameter).SequenceEqual(
            [AppPage.Library, AppPage.Albums, AppPage.Artists, AppPage.Playlists, AppPage.NowPlaying, AppPage.Settings]),
            "Settings follows Now Playing in the navigation");
        var settings = navigation.Last();
        Check(Descendants(settings).OfType<MusicPlayer.Controls.LucideIcon>().Any(), "Settings navigation includes an icon");
        Check(!Descendants(content).OfType<Button>().Any(b => Equals(b.Content, "Discord")), "Discord has no separate header button");
        settings.Command.Execute(settings.CommandParameter);
        content.UpdateLayout();
        Check(vm.SelectedPage == AppPage.Settings && Descendants(content).OfType<MusicPlayer.Views.SettingsView>().Any(),
            "Settings navigation opens the consolidated settings page");
        window.Close();
    }

    private static void CheckLibraryWorkflowLayout()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        vm.Tracks.Add(Track("A"));
        vm.Tracks.Add(Track("B"));
        vm.CreatePlaylistCommand.Execute(null);
        vm.SelectedPlaylist!.AddTracks(vm.Tracks);
        vm.CancelRenamePlaylistCommand.Execute(null);
        var settings = new SettingsView { DataContext = vm };
        var library = new LibraryView { DataContext = vm };
        var playlists = new PlaylistsView { DataContext = vm };
        foreach (var view in new UserControl[] { settings, library, playlists })
        {
            view.Measure(new Size(850, 430));
            view.Arrange(new Rect(0, 0, 850, 430));
            view.UpdateLayout();
            Check(view.ActualWidth == 850 && view.ActualHeight == 430, $"{view.GetType().Name} loads at compact size");
        }
        var list = (ListBox)playlists.FindName("PlaylistTracks");
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).IsSelected = true;
        var queue = Descendants(playlists).OfType<Button>().Single(b => Equals(b.ToolTip, "Queue selected tracks"));
        queue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(vm.Queue.Count == 2, "Playlist toolbar queues every selected row");
        var remove = Descendants(playlists).OfType<Button>().Single(b => Equals(b.ToolTip, "Remove selected tracks from playlist (Delete)"));
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(vm.SelectedPlaylist.Tracks.Count == 0 && list.Items.Count == 0, "Bulk removal updates the visible empty playlist");
    }

    private static void CheckLibraryWorkflows()
    {
        using var directory = new TemporaryTestDirectory("workflows");
        var root = directory.Path;
        var a = new Track { FilePath = Path.Combine(root, "first.mp3"), Title = "First song", Artist = "Different artist", Genre = "Jazz", Year = 2025 };
        var b = new Track { FilePath = Path.Combine(root, "second.mp3"), Title = "Second song", Artist = "Other artist", Genre = "Rock", Year = 2024 };
        File.WriteAllBytes(a.FilePath, []);
        File.WriteAllBytes(b.FilePath, []);
        Check(TrackSearch.Matches(a, "first different genre:jazz year:2025") &&
              TrackSearch.Matches(a, "title:\"First song\" artist:different") &&
              !TrackSearch.Matches(a, "first genre:rock"), "Search combines words, quoted phrases and metadata fields");
        Check(TrackSearch.Matches(new Track { FilePath = a.FilePath, Title = "Time: After dark" }, "\"Time: After\""),
            "Colons inside quoted titles remain searchable as literal text");
        var playlist = new Playlist([a, b, a]) { Name = "Transfer" };
        var database = new SqliteMusicStore(Path.Combine(root, "music.db"));
        ((IPlaylistStore)database).Save([playlist]);
        var preferences = new JsonUiPreferencesStore(Path.Combine(root, "preferences.json"));
        using (var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                   playlistStore: database, libraryStore: database, uiPreferencesStore: preferences))
        {
            vm.SelectedPlaylist = vm.Playlists.Single();
            Check(vm.PlaylistTrackRows.Count == 3 && vm.PlaylistTrackRows.Distinct().Count() == 3,
                "Duplicate playlist occurrences have independent selection identities");
            var indices = vm.MovePlaylistTracks([2], -1);
            Check(indices.SequenceEqual([1]) && vm.SelectedPlaylist.Tracks.Select(t => t.Title).SequenceEqual([a.Title, a.Title, b.Title]),
                "Manual ordering moves a duplicate occurrence independently");
            vm.RemovePlaylistTracks([0, 1]);
            Check(vm.SelectedPlaylist.Tracks.Single().Title == b.Title && ((IPlaylistStore)database).Load().Single().Tracks.Count == 1,
                "Bulk removal saves selected occurrences once");
            vm.TryAddTracksToPlaylist([a, a], vm.SelectedPlaylist);
            Check(((IPlaylistStore)database).Load().Single().Tracks.Select(t => t.Title).SequenceEqual([b.Title, a.Title, a.Title]),
                "Bulk playlist addition preserves order and duplicates");
            vm.LibrarySort = LibrarySort.Title;
            vm.LibrarySortDescending = true;
            vm.AutomaticScanning = false;
            Check(vm.LibraryTracks.Cast<Track>().First().Title == b.Title, "Configurable descending sorting controls library results");
            vm.QueueTracks([a, b]);
            vm.QueueTracks([b, a], playNext: true);
            Check(vm.Queue.SequenceEqual([b, a, a, b]), "Bulk queue and play-next preserve selected order");
        }
        using (var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                   playlistStore: database, libraryStore: database, uiPreferencesStore: preferences))
            Check(restored.LibrarySort == LibrarySort.Title && restored.LibrarySortDescending && !restored.AutomaticScanning,
                "Sorting and automatic scanning survive restart");
        var export = Path.Combine(root, "transfer.m3u8");
        PlaylistExportService.Export(playlist, export, relativePaths: true);
        var lines = File.ReadAllLines(export);
        Check(lines.Where(l => !l.StartsWith('#')).SequenceEqual(["first.mp3", "second.mp3", "first.mp3"]),
            "M3U8 export preserves relative paths, ordering and duplicates");
        var imported = new PlaylistImportService(new Metadata()).ImportFileAsync(export).GetAwaiter().GetResult();
        Check(imported.Tracks.Select(t => t.FilePath).SequenceEqual([a.FilePath, b.FilePath, a.FilePath]),
            "Exported playlists round-trip through the local playlist importer");
        var unicode = new Track { FilePath = Path.Combine(root, "#" + char.ConvertFromUtf32(233) + ".mp3"), Title = "Unicode " + char.ConvertFromUtf32(233) };
        File.WriteAllBytes(unicode.FilePath, []);
        PlaylistExportService.Export(new Playlist([unicode]), export, relativePaths: true);
        var unicodeImported = new PlaylistImportService(new Metadata()).ImportFileAsync(export).GetAwaiter().GetResult();
        Check(unicodeImported.Tracks.Single().FilePath == unicode.FilePath,
            "UTF-8 exports preserve Unicode and escape relative paths starting with a comment marker");
        using (var failed = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
                   playlistStore: new WorkflowFailingStore(new Playlist([a, b]) { Name = "Original" })))
        {
            failed.SelectedPlaylist = failed.Playlists.Single();
            failed.BeginRenamePlaylistCommand.Execute(null);
            failed.PlaylistName = "Changed";
            failed.RenamePlaylistCommand.Execute(null);
            Check(failed.SelectedPlaylist.Name == "Original" && failed.IsRenamingPlaylist && failed.PlaylistError is not null,
                "Failed rename restores the saved name and leaves the editor available for retry");
            failed.RemovePlaylistTracks([0, 1]);
            Check(failed.SelectedPlaylist.Tracks.SequenceEqual([a, b]), "Failed bulk removal restores all original occurrences");
            failed.TryAddTracksToPlaylist([a], failed.SelectedPlaylist);
            Check(failed.SelectedPlaylist.Tracks.SequenceEqual([a, b]), "Failed bulk addition cannot duplicate tracks on retry");
        }
        database.SaveMusicFolder(new(root, true));
        database.RemoveMusicFolder(root);
        Check(database.LoadMusicFolders().Count == 0 && ((IPlaylistStore)database).Load().Single().Tracks.Count == 3,
            "Removing a watched root persists without removing songs or playlist entries");
        var resets = 0;
        playlist.Tracks.CollectionChanged += (_, _) => resets++;
        playlist.ReplaceTracks([]);
        Check(resets == 1 && playlist.Tracks.Count == 0, "Empty playlist replacement publishes a collection notification");
    }
    private sealed class WorkflowFailingStore(Playlist playlist) : IPlaylistStore
    {
        public IReadOnlyList<Playlist> Load() => [playlist];
        public void Save(IEnumerable<Playlist> playlists) => throw new IOException("Test write failure");
    }

}
