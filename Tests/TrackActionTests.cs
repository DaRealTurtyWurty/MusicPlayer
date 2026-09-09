using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static void CheckTrackActions()
    {
        var locations = new RecordingFileLocationService();
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            fileLocationService: locations);
        var first = new Track { FilePath = "first", Title = "First", Artist = "Artist A", Album = "Shared title" };
        var second = new Track { FilePath = "second", Title = "Second", Artist = "Artist B", Album = "Shared title" };
        var unknown = new Track { FilePath = "unknown", Title = "Unknown" };
        foreach (var track in new[] { first, second, unknown }) vm.Tracks.Add(track);
        vm.SelectedTrack = first;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.EnqueueTrackCommand.Execute(second);
        vm.EnqueueTrackCommand.Execute(second);
        vm.PlayTrackNextCommand.Execute(first);
        Check(vm.Queue.SequenceEqual(new[] { first, second, second }) && vm.CurrentTrack == first && player.PlayCount == 1,
            "Track actions preserve duplicate entries and never interrupt playback");
        Check(!vm.ShowAlbumCommand.CanExecute(null) && !vm.ShowArtistCommand.CanExecute(null) &&
              !vm.OpenFileLocationCommand.CanExecute(null) && !vm.EnqueueTrackCommand.CanExecute(null),
            "Track actions require a target");

        vm.SelectedPage = AppPage.Albums;
        vm.MusicSearchText = "no results";
        vm.SelectedReleaseFilter = vm.ReleaseFilters[1];
        vm.ShowAlbumCommand.Execute(second);
        Check(vm.SelectedPage == AppPage.Albums && vm.SelectedAlbum?.Artist == "Artist B" &&
              vm.SelectedBrowseTrack == second && vm.MusicSearchText == "" && vm.SelectedReleaseFilter.Type is null,
            "Show album scopes identical titles by artist and resets filters even on the same page");
        vm.ShowArtistCommand.Execute(first);
        Check(vm.SelectedPage == AppPage.Artists && vm.SelectedArtist?.Name == "Artist A" &&
              vm.SelectedAlbum is null && !vm.ShowArtistTracks && vm.VisibleMusicGroups.Single().Tracks.Single() == first,
            "Show artist opens the target artist's releases without stale album state");
        vm.ShowAlbumCommand.Execute(unknown);
        Check(vm.SelectedAlbum?.Name == "Unknown album", "Untagged tracks open the unknown album group");
        vm.ShowArtistCommand.Execute(unknown);
        Check(vm.SelectedArtist?.Name == "Unknown artist", "Untagged tracks open the unknown artist group");
        var external = new Track { FilePath = "external", Title = "External", Artist = "External artist", Album = "External album" };
        vm.Tracks.Add(new Track { FilePath = "another", Title = "Another" });
        vm.ShowAlbumCommand.Execute(external);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(vm.BrowseTracks.Single() == external && !vm.Tracks.Contains(external),
            "Queue tracks outside the library remain browsable after pending updates without changing library membership");

        vm.OpenFileLocationCommand.Execute(second);
        Check(locations.LastPath == second.FilePath && vm.PlaybackError is null, "File location receives the targeted file path");
        locations.Fail = true;
        vm.OpenFileLocationCommand.Execute(first);
        Check(vm.PlaybackError?.Contains("First") == true && vm.CurrentTrack == first && vm.IsPlaying,
            "File location failures are reported without interrupting playback");
        using (var temp = new TemporaryTestDirectory("TrackLocations"))
        {
            try
            {
                new FileLocationService().ShowFile(Path.Combine(temp.Path, "missing.mp3"));
                Check(false, "Missing files must not launch Explorer");
            }
            catch (FileNotFoundException ex)
            {
                Check(ex.Message.Contains("Locate"), "Missing file locations explain how to recover the track");
            }
        }

        void Layout(FrameworkElement view)
        {
            view.Measure(new Size(884, 560));
            view.Arrange(new Rect(0, 0, 884, 560));
            view.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        void VerifyMenu(FrameworkElement row, Track target)
        {
            var menu = row.ContextMenu;
            Check(menu is not null, "Every track row exposes a context menu");
            menu!.PlacementTarget = row;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            foreach (var label in new[] { "Play _next", "Add to _queue", "Add to _playlist…", "Show _album", "Show a_rtist", "Open file _location" })
                Check(items.Count(i => Equals(i.Header, label)) == 1, $"Track menu includes {label} exactly once");
            var showAlbum = items.Single(i => Equals(i.Header, "Show _album"));
            Check(ReferenceEquals(showAlbum.Command, vm.ShowAlbumCommand) && ReferenceEquals(showAlbum.CommandParameter, target),
                "Detached context menu binds navigation to its row, independent of selected tracks");
            var location = items.Single(i => Equals(i.Header, "Open file _location"));
            Check(ReferenceEquals(location.Command, vm.OpenFileLocationCommand) && ReferenceEquals(location.CommandParameter, target),
                "Detached context menu binds file location to its row");
            var append = items.Single(i => Equals(i.Header, "Add to _queue"));
            if (append.Command is not null)
            {
                append.Command.Execute(append.CommandParameter);
                Check(vm.Queue.Last() == target, "Context menu queues its row despite a different active selection");
            }
        }

        var library = new LibraryView { DataContext = vm };
        Layout(library);
        var libraryList = (ListView)library.FindName("TrackList");
        VerifyMenu((FrameworkElement)libraryList.ItemContainerGenerator.ContainerFromItem(second), second);
        vm.ShowAlbumCommand.Execute(second);
        var browser = new MusicBrowserView { DataContext = vm };
        Layout(browser);
        var browserList = (ListView)browser.FindName("MusicTrackList");
        VerifyMenu((FrameworkElement)browserList.ItemContainerGenerator.ContainerFromItem(second), second);
        vm.CreatePlaylistCommand.Execute(null);
        vm.SelectedPlaylist!.Tracks.Add(first);
        vm.SelectedPlaylist.Tracks.Add(second);
        vm.SelectedPlaylistTrackIndex = 0;
        var playlists = new PlaylistsView { DataContext = vm };
        Layout(playlists);
        var playlistList = (ListBox)playlists.FindName("PlaylistTracks");
        VerifyMenu((FrameworkElement)playlistList.ItemContainerGenerator.ContainerFromIndex(1), second);
        var queue = new QueuePanel { DataContext = vm };
        Layout(queue);
        var queueList = (ListBox)queue.FindName("QueueList");
        var currentEntry = vm.QueueTimeline.Single(e => e.IsCurrent);
        VerifyMenu((FrameworkElement)queueList.ItemContainerGenerator.ContainerFromItem(currentEntry), first);
        var upcomingEntry = vm.QueueTimeline.First(e => e.Kind == QueueEntryKind.Upcoming && e.Track == second);
        VerifyMenu((FrameworkElement)queueList.ItemContainerGenerator.ContainerFromItem(upcomingEntry), second);
        Console.WriteLine("Track context menu action checks passed.");
    }

    private sealed class RecordingFileLocationService : IFileLocationService
    {
        public string? LastPath { get; private set; }
        public bool Fail { get; set; }
        public void ShowFile(string filePath)
        {
            if (Fail) throw new IOException("Explorer is unavailable.");
            LastPath = filePath;
        }
    }
}
