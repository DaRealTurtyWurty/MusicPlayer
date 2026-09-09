using System.IO;
using System.Windows;
using System.Windows.Controls;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckLibraryMaintenanceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MusicPlayerMaintenanceTests", Guid.NewGuid().ToString("N"));
        var music = Path.Combine(root, "music");
        var moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(moved);
        var oldPath = Path.Combine(music, "original.mp3");
        var newPath = Path.Combine(moved, "renamed.mp3");
        var otherPath = Path.Combine(music, "other.mp3");
        File.WriteAllText(oldPath, "song");
        File.WriteAllText(otherPath, "other");
        var metadata = new LibraryRefreshTests.RefreshMetadata();
        metadata.Tags[oldPath] = ("Original", "Artist");
        metadata.Tags[newPath] = ("Original", "Artist");
        metadata.Tags[otherPath] = ("Other", "Artist");
        var service = new LibraryRefreshService(metadata);
        var original = await service.ReadFileAsync(oldPath, default);
        var other = await service.ReadFileAsync(otherPath, default);
        var store = new SqliteMusicStore(Path.Combine(root, "music.db"));
        store.SavePlaylists([new Playlist([original, original, other]) { Name = "Keep my songs" }]);
        store.SaveSession(new PlaybackSession(original, TimeSpan.FromSeconds(22), [original, other, original])
        { History = [new(original, false)] });
        File.Move(oldPath, newPath);
        var picker = new LocatePicker { AudioFile = newPath, Folder = moved };
        var matchPicker = new MatchPicker();
        var player = new FakePlayer();
        using (var vm = new MainViewModel(picker, picker, metadata, new Scanner(), player,
            playlistStore: store, libraryStore: store, playbackSessionStore: store,
            libraryRefreshService: service, trackMatchPicker: matchPicker, monitorLibrary: false))
        {
            vm.SelectedTrack = vm.Tracks.Single(t => t.FilePath == oldPath);
            vm.SelectedQueueIndex = 2;
            await vm.RefreshLibraryAsync();
            var missing = vm.Tracks.Single(t => t.FilePath == oldPath);
            Check(missing.IsMissing && vm.CurrentTrack!.IsMissing && vm.Queue[0].IsMissing &&
                  vm.QueueTimeline.First().Track.IsMissing && vm.Playlists.Single().Tracks[0].IsMissing,
                "Refresh updates missing availability across library, playlists, current song, queue and history");
            Check(vm.SelectedTrack == missing && vm.SelectedQueueIndex == 2 && vm.PositionSeconds == 22,
                "Background refresh preserves selections and playback position");
            CheckLocateMenus(vm, missing);
            vm.PlayCommand.Execute(null);
            var plays = player.PlayCount;
            await vm.LocateFileCommand.ExecuteAsync(missing);
            Check(vm.CurrentTrack!.FilePath == newPath && vm.IsPlaying && player.PlayCount == plays && vm.PositionSeconds == 22 &&
                  vm.Queue.Count == 3 && vm.Queue.Count(t => t.FilePath == newPath) == 2 &&
                  vm.Playlists.Single().Tracks.Count == 3 && vm.Playlists.Single().Tracks[0].FilePath == newPath &&
                  vm.QueueTimeline.First().Track.FilePath == newPath && vm.Tracks.All(t => !t.IsMissing),
                "Choose-file Locate updates every occurrence and keeps active playback uninterrupted");
            vm.PauseCommand.Execute(null);
            vm.PlayCommand.Execute(null);
            Check(vm.CurrentTrack.FilePath == newPath && player.Position.TotalSeconds == 22,
                "Resuming a relocated current song reloads its file at the existing position");

            File.Move(otherPath, Path.Combine(root, "other.offline"));
            await vm.RefreshLibraryAsync();
            var missingOther = vm.Tracks.Single(t => t.FilePath == otherPath);
            await vm.LocateFolderCommand.ExecuteAsync(missingOther);
            Check(vm.LocateStatus!.StartsWith("No matching") && vm.Tracks.Any(t => t.FilePath == otherPath && t.IsMissing),
                "Folder Locate with no matching title and artist keeps the missing track");
            var match1 = Path.Combine(moved, "match1.mp3");
            var match2 = Path.Combine(moved, "match2.mp3");
            File.WriteAllText(match1, "first"); File.WriteAllText(match2, "second");
            metadata.Tags[match1] = ("Other", "Artist"); metadata.Tags[match2] = (" other ", "ARTIST");
            await vm.LocateFolderCommand.ExecuteAsync(missingOther);
            Check(matchPicker.SeenMatches == 2 && vm.Tracks.Any(t => t.FilePath == otherPath),
                "Ambiguous folder matches require a choice and cancelling preserves the original track");
            matchPicker.Selection = 1;
            await vm.LocateFolderCommand.ExecuteAsync(missingOther);
            Check(vm.Queue.Single(t => t.Title.Trim().Equals("other", StringComparison.OrdinalIgnoreCase)).FilePath == match2 &&
                  !store.LoadLibrary().Any(t => t.FilePath == otherPath),
                "Folder Locate uses the chosen metadata match and persists the replacement path");
        }
        var saved = store.LoadSession();
        Check(saved.CurrentTrack!.FilePath == newPath && saved.Queue.Count == 3 && saved.History.Single().Track.FilePath == newPath,
            "Located file paths and history survive closing and restoring the session");

        // Exercise the production debounce and background watcher scheduling on the dispatcher.
        var livePath = Path.Combine(root, "live");
        Directory.CreateDirectory(livePath);
        var liveStore = new SqliteMusicStore(Path.Combine(root, "live.db"));
        liveStore.SaveMusicFolder(new WatchedMusicFolder(livePath, true));
        using var live = new MainViewModel(picker, picker, metadata, new Scanner(), new FakePlayer(),
            libraryStore: liveStore, libraryRefreshService: service);
        await WaitUntilAsync(() => live.LibraryRefreshStatus?.StartsWith("Library up to date") == true);
        Check(live.Tracks.Count == 0 && !live.HasToast, "Startup refresh monitors a saved empty music folder without a notification");
        var discovered = Path.Combine(livePath, "discovered.mp3");
        File.WriteAllText(discovered, "new music");
        await WaitUntilAsync(() => live.Tracks.Any(t => t.FilePath == discovered));
        Check(live.Queue.Count == 0 && live.CurrentTrack is null,
            "Watchers discover newly copied songs without modifying the queue or starting playback");
        await WaitUntilAsync(() => !live.IsRefreshingLibrary);
        metadata.Tags[discovered] = ("Retagged", "Updated artist");
        for (var i = 0; i < 3; i++) { File.AppendAllText(discovered, " edit"); await Task.Delay(80); }
        await WaitUntilAsync(() => live.Tracks.Single().Title == "Retagged");
        Check(live.Tracks.Single().Artist == "Updated artist", "Debounced watcher refresh applies new title and artist metadata");
        await WaitUntilAsync(() => !live.IsRefreshingLibrary);
        Check(!live.HasToast, "Automatic watcher scans stay quiet");
        await live.RescanLibraryCommand.ExecuteAsync(null);
        Check(liveStore.LoadLibrary().Single().Title == "Retagged", "Manual Rescan library persists refreshed metadata");
        Check(live.HasToast && live.ToastMessage!.StartsWith("Scan complete"), "Manual scan completion produces a toast");
        var toastWindow = new MainWindow(live);
        var toastContent = (FrameworkElement)toastWindow.Content;
        SaveMaintenancePreview(toastContent, "scan-toast.png", 884, 561);
        Check(((FrameworkElement)toastWindow.FindName("NotificationToast")).Visibility == Visibility.Visible &&
              !Descendants(toastContent).OfType<TextBlock>().Any(t => t.Text.StartsWith("Library up to date")),
            "Scan results appear in an overlay toast rather than a persistent library summary");
        await WaitUntilAsync(() => !live.HasToast);
        Check(true, "Scan completion toast expires automatically");
        toastWindow.DataContext = null;
        toastWindow.Close();
    }

    private static void CheckLocateMenus(MainViewModel vm, Track missing)
    {
        var library = new LibraryView { DataContext = vm };
        library.Measure(new Size(884, 440)); library.Arrange(new Rect(0, 0, 884, 440)); library.UpdateLayout();
        var list = (ListView)library.FindName("TrackList");
        var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(missing);
        row.ContextMenu.PlacementTarget = row;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var locate = row.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "_Locate"));
        var chooseFile = (MenuItem)locate.Items[0];
        Check(locate.Visibility == Visibility.Visible && ReferenceEquals(chooseFile.Command, vm.LocateFileCommand) &&
              ReferenceEquals(chooseFile.CommandParameter, missing), "Missing library rows expose Locate with the correct track parameter");
        SaveMaintenancePreview(library, "missing-library.png", 884, 440);
        var queue = new QueuePanel { DataContext = vm };
        queue.Measure(new Size(296, 560)); queue.Arrange(new Rect(0, 0, 296, 560)); queue.UpdateLayout();
        var queueList = (ListBox)queue.FindName("QueueList");
        var queueRow = (ListBoxItem)queueList.ItemContainerGenerator.ContainerFromIndex(0);
        queueRow.ContextMenu.PlacementTarget = queueRow;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        locate = queueRow.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "_Locate"));
        chooseFile = (MenuItem)locate.Items[0];
        Check(locate.Visibility == Visibility.Visible && ReferenceEquals(chooseFile.Command, vm.LocateFileCommand) &&
              ReferenceEquals(chooseFile.CommandParameter, queueRow.DataContext is QueueEntry entry ? entry.Track : null),
            "Missing history rows can locate their underlying track without changing queue selection");
        vm.OpenPlaylistCommand.Execute(vm.Playlists.Single());
        var playlists = new PlaylistsView { DataContext = vm };
        playlists.Measure(new Size(884, 540)); playlists.Arrange(new Rect(0, 0, 884, 540)); playlists.UpdateLayout();
        var playlistList = (ListBox)playlists.FindName("PlaylistTracks");
        var playlistRow = (ListBoxItem)playlistList.ItemContainerGenerator.ContainerFromIndex(0);
        playlistRow.ContextMenu.PlacementTarget = playlistRow;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        locate = playlistRow.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "_Locate"));
        Check(ReferenceEquals(((MenuItem)locate.Items[1]).Command, vm.LocateFolderCommand), "Missing playlist rows offer folder lookup");
        var chooser = new LocateTrackWindow(missing, [missing, missing]);
        var content = (FrameworkElement)chooser.Content;
        content.Measure(new Size(680, 400)); content.Arrange(new Rect(0, 0, 680, 400)); content.UpdateLayout();
        SaveMaintenancePreview(content, "locate-matches.png", 680, 400);
        Check(!((Button)chooser.FindName("UseFileButton")).IsEnabled && ((ListBox)chooser.FindName("Matches")).Items.Count == 2,
            "Ambiguous-match dialog displays choices and requires an explicit selection");
        ((ListBox)chooser.FindName("Matches")).SelectedIndex = 1;
        Check(((Button)chooser.FindName("UseFileButton")).IsEnabled, "Choosing a candidate enables file confirmation");
        chooser.Close();
        library.DataContext = null; queue.DataContext = null; playlists.DataContext = null;
    }

    private static void SaveMaintenancePreview(FrameworkElement content, string name, int width, int height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Background refresh did not finish");
            await Task.Delay(100);
        }
    }

    private sealed class LocatePicker : IFilePickerService, IFolderPickerService
    {
        public string? AudioFile { get; set; }
        public string? Folder { get; set; }
        public string? PickAudioFile() => AudioFile;
        public string? PickMusicFolder() => Folder;
        public IReadOnlyList<string> PickAudioFiles() => [];
        public string? PickPlaylistFile() => null;
    }

    private sealed class MatchPicker : ITrackMatchPicker
    {
        public int? Selection { get; set; }
        public int SeenMatches { get; private set; }
        public Track? PickMatch(Track missing, IReadOnlyList<Track> matches)
        {
            SeenMatches = matches.Count;
            return Selection is { } index ? matches[index] : null;
        }
    }
}
