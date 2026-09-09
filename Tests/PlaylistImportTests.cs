using System.IO;
using System.Text;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static class PlaylistImportTests
{
    public static async Task RunAsync()
    {
        using var temporaryDirectory = new TemporaryTestDirectory("MusicPlayerImportTests");
        var directory = temporaryDirectory.Path;
        var album = Path.Combine(directory, "Album");
        Directory.CreateDirectory(album);
        var first = Path.Combine(album, "01 café.WAV");
        var second = Path.Combine(album, "02 song.flac");
        var broken = Path.Combine(album, "03 broken.mp3");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);
        File.WriteAllBytes(broken, []);
        File.WriteAllText(Path.Combine(album, "cover.jpg"), "not audio");
        var playlistPath = Path.Combine(directory, "Evening.m3u8");
        File.WriteAllText(playlistPath, $"#EXTM3U\n#EXTINF:120,A title\nAlbum/01 café.WAV\n\n{second}\n{new Uri(first).AbsoluteUri}\nmissing.mp3\nhttps://example.com/song.mp3\nAlbum/03 broken.mp3\nAlbum/cover.jpg\n", new UTF8Encoding(true));
        var importer = new PlaylistImportService(new Metadata());
        var reports = new ProgressRecorder();
        var result = importer.ImportFileAsync(playlistPath, reports).GetAwaiter().GetResult();
        Check(reports.Values.First().Total is null && reports.Values.Last() is { Processed: 7, Total: 7, Skipped: 4 },
            "M3U8 progress moves from discovery to completion, counting skipped entries");
        Check(result.Name == "Evening" && result.Tracks.Select(t => t.FilePath).SequenceEqual(new[] { first, second, first }),
            "M3U8 handles UTF-8 BOM, Unicode, relative/absolute paths, file URIs, order and duplicates");
        Check(result.SkippedCount == 4, "M3U8 reports missing, remote, unsupported and corrupt entries");
        reports.Values.Clear();
        var folderResult = importer.ImportFolderAsync(directory, reports).GetAwaiter().GetResult();
        Check(reports.Values.First().Total is null && reports.Values.Last() is { Processed: 3, Total: 3, Skipped: 1 },
            "Folder progress reports a discovered total and finishes at 100 percent");
        Check(folderResult.Tracks.Select(t => t.FilePath).SequenceEqual(new[] { first, second }) && folderResult.SkippedCount == 1,
            "Folder imports recurse, sort, accept uppercase extensions and skip corrupt audio");
        var hls = Path.Combine(directory, "stream.m3u8");
        File.WriteAllText(hls, "#EXTM3U\n#EXT-X-TARGETDURATION:10\nsegment.ts\n");
        try { importer.ImportFileAsync(hls).GetAwaiter().GetResult(); throw new Exception("Expected an HLS import error"); }
        catch (InvalidDataException) { Check(true, "Streaming manifests get a clear import error"); }

        var picker = new Picker { FilePath = playlistPath, FolderPath = directory };
        var player = new Player();
        IPlaylistStore store = new SqliteMusicStore(Path.Combine(directory, "music.db"));
        using var vm = new MainViewModel(picker, picker, new Metadata(), new LibraryScanner(new Metadata()), player,
            playlistStore: store, playlistImportService: importer);
        vm.SelectedTrack = result.Tracks[0];
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(result.Tracks[1]);
        var current = vm.CurrentTrack;
        await vm.ImportPlaylistFileCommand.ExecuteAsync(null);
        Check(vm.SelectedPage == AppPage.Playlists && vm.IsPlaylistOpen && vm.SelectedPlaylist!.Tracks.Count == 3 && !vm.IsRenamingPlaylist,
            "M3U8 import creates and opens a populated playlist");
        Check(store.Load().Single().Tracks.Count == 3 && vm.ToastMessage!.Contains("Skipped 4"),
            "Imported playlists save and show skipped-entry feedback");
        Check(vm.Tracks.Count == 2 && vm.LibraryTracks.Count == 2,
            "Playlist imports also populate the library without duplicate songs");
        Check(vm.CurrentTrack == current && vm.IsPlaying && vm.Queue.Count == 1 && player.PlayCount == 1,
            "Import leaves current playback and queue intact");
        await vm.ImportPlaylistFolderCommand.ExecuteAsync(null);
        Check(vm.Playlists.Count == 2 && vm.SelectedPlaylist!.Tracks.Count == 2 && store.Load().Count == 2,
            "Folder import creates a separate saved playlist");
        picker.FilePath = null;
        await vm.ImportPlaylistFileCommand.ExecuteAsync(null);
        Check(vm.Playlists.Count == 2 && !vm.IsImportingPlaylist, "Canceling the picker changes nothing");
        picker.FilePath = hls;
        await vm.ImportPlaylistFileCommand.ExecuteAsync(null);
        Check(vm.Playlists.Count == 2 && vm.PlaylistError is not null && !vm.IsImportingPlaylist,
            "Failed imports report errors without creating partial playlists");
        var empty = Path.Combine(directory, "empty.m3u8");
        File.WriteAllText(empty, "#EXTM3U\nmissing.mp3\n");
        picker.FilePath = empty;
        await vm.ImportPlaylistFileCommand.ExecuteAsync(null);
        Check(vm.Playlists.Count == 2 && vm.ToastMessage!.StartsWith("No tracks imported"),
            "Empty imports explain the result without adding empty playlists");

        var target = vm.Playlists[0];
        vm.OpenPlaylistCommand.Execute(target);
        var originalTracks = target.Tracks.ToArray();
        var changed = 0;
        target.Tracks.CollectionChanged += (_, _) => changed++;
        picker.AudioPaths = [second, first, broken, Path.Combine(directory, "missing.mp3"), Path.Combine(album, "cover.jpg")];
        await vm.AddTracksToPlaylistCommand.ExecuteAsync(null);
        Check(ReferenceEquals(vm.SelectedPlaylist, target) && vm.SelectedPage == AppPage.Playlists && vm.IsPlaylistOpen && vm.Playlists.Count == 2,
            "Add tracks keeps the existing playlist open without navigating Library or creating a playlist");
        Check(target.Tracks.Take(3).SequenceEqual(originalTracks) && target.Tracks.Skip(3).Select(t => t.FilePath).SequenceEqual(new[] { second, first }) && changed == 1,
            "Multiple selected files append in picker order with one collection refresh");
        Check(store.Load().Select(p => p.Id).SequenceEqual(vm.Playlists.Select(p => p.Id)) && store.Load()[0].Tracks.Count == 5 && store.Load()[1].Tracks.Count == 2 &&
              vm.ToastMessage!.StartsWith("Added 2 tracks") && vm.ToastMessage.Contains("Skipped 3"),
            "Adding tracks persists the target playlist and reports unsupported, missing and unreadable files");
        Check(vm.CurrentTrack == current && vm.IsPlaying && vm.Queue.Count == 1 && player.PlayCount == 1,
            "Adding tracks preserves current playback and queue");
        picker.AudioPaths = [];
        await vm.AddTracksToPlaylistCommand.ExecuteAsync(null);
        Check(target.Tracks.Count == 5 && changed == 1 && !vm.IsImportingPlaylist, "Canceling Add tracks leaves the playlist unchanged");
        picker.AudioPaths = [broken];
        await vm.AddTracksToPlaylistCommand.ExecuteAsync(null);
        Check(target.Tracks.Count == 5 && changed == 1 && !vm.IsImportingPlaylist && !vm.IsAddingPlaylistTracks,
            "An entirely unreadable selection leaves the playlist unchanged and closes the loading modal");

        var addingImporter = new DeferredImporter();
        using var adding = new MainViewModel(picker, picker, new Metadata(), new LibraryScanner(new Metadata()), new Player(), playlistImportService: addingImporter);
        Check(!adding.AddTracksToPlaylistCommand.CanExecute(null), "Add tracks requires an editable playlist");
        adding.CreatePlaylistCommand.Execute(null);
        var addingOperation = adding.AddTracksToPlaylistCommand.ExecuteAsync(null);
        addingImporter.Progress!.Report(new(1, 3, 0, "Song.flac"));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(adding.IsImportingPlaylist && adding.IsAddingPlaylistTracks && adding.ImportProgressLabel.StartsWith("Adding tracks") &&
              !adding.ImportPlaylistFileCommand.CanExecute(null) && !adding.AddTracksToPlaylistCommand.CanExecute(null),
            "Add tracks shows modal progress and prevents overlapping imports");
        addingImporter.Completion.SetResult(result);
        await addingOperation;
        Check(!adding.IsImportingPlaylist && !adding.IsAddingPlaylistTracks && adding.AddTracksToPlaylistCommand.CanExecute(null),
            "Add tracks restores its command and closes the modal on completion");

        var deferred = new DeferredImporter();
        using var pending = new MainViewModel(picker, picker, new Metadata(), new LibraryScanner(new Metadata()), new Player(), playlistImportService: deferred);
        var operation = pending.ImportPlaylistFileCommand.ExecuteAsync(null);
        Check(pending.IsImportingPlaylist && !pending.ImportPlaylistFolderCommand.CanExecute(null), "Both import actions are disabled during an import");
        Check(pending.IsImportDiscovering, "Import initially shows animated discovery progress");
        deferred.Progress!.Report(new(25, 100, 2, "Current song.flac"));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(!pending.IsImportDiscovering && pending.ImportProgressPercent == 25 && pending.ImportProgressLabel.Contains("25 of 100") && pending.ImportProgressDetail.Contains("Current song.flac"),
            "Import progress exposes the live count, percentage, filename and skipped entries");
        pending.NavigateCommand.Execute(AppPage.Library);
        Check(pending.IsImportingPlaylist && pending.ImportProgressPercent == 25, "Progress remains available across views");
        deferred.Completion.SetResult(result);
        await operation;
        Check(!pending.IsImportingPlaylist && pending.ImportPlaylistFolderCommand.CanExecute(null), "Import actions recover after completion");
        var finishedProgress = pending.ImportProgress;
        deferred.Progress.Report(new(30, 100, 2, "Late update.flac"));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Check(pending.ImportProgress == finishedProgress, "Late progress updates are ignored after completion");

        var savingImporter = new DeferredImporter();
        savingImporter.Completion.SetResult(result);
        using var slowStore = new SlowStore();
        using var saving = new MainViewModel(picker, picker, new Metadata(), new LibraryScanner(new Metadata()), new Player(),
            playlistStore: slowStore, playlistImportService: savingImporter);
        var savingOperation = saving.ImportPlaylistFileCommand.ExecuteAsync(null);
        try
        {
            Check(await Task.Run(() => slowStore.Entered.Wait(TimeSpan.FromSeconds(10))), "Import reaches background persistence");
            Check(saving.IsImportingPlaylist && saving.IsFinishingPlaylistImport && !savingOperation.IsCompleted &&
                  saving.ImportProgressLabel == "Saving playlist…" && saving.ImportProgressPercent == 100,
                "Modal remains active at 100 percent while slow persistence runs off the caller thread");
            var savingProgress = saving.ImportProgress;
            savingImporter.Progress!.Report(new(1, 100, 0, "Late track.flac"));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Check(saving.ImportProgress == savingProgress, "Queued track progress cannot overwrite the finishing phase");
        }
        finally { slowStore.Release.Set(); }
        await savingOperation;
        Check(!saving.IsImportingPlaylist && !saving.IsFinishingPlaylistImport && saving.SelectedPlaylist!.Tracks.Count == 3,
            "Modal closes and playlist opens after persistence finishes");

        var realFolder = Path.Combine(directory, "Silent audio");
        FixtureLibrary.Write(realFolder);
        File.WriteAllText(Path.Combine(realFolder, "Selected.m3u8"), "#EXTM3U\n02.wav\n01.wav\n");
        var realImport = new PlaylistImportService(new TagLibMetadataService()).ImportFileAsync(Path.Combine(realFolder, "Selected.m3u8")).GetAwaiter().GetResult();
        Check(realImport.Tracks.Select(t => t.Title).SequenceEqual(new[] { "Weightless", "A Walk" }) && realImport.Tracks.All(t => t.Duration > TimeSpan.Zero),
            "Import reads actual embedded music metadata");
        await PlaylistArtworkTests.RunAsync(realFolder);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
    private sealed class Metadata : IMetadataService
    {
        public Track ReadTrack(string path) => path.Contains("broken") ? throw new InvalidDataException() : new Track
        { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Duration = TimeSpan.FromMinutes(2) };
    }
    private sealed class Picker : IFilePickerService, IFolderPickerService
    {
        public IReadOnlyList<string> AudioPaths { get; set; } = [];
        public IReadOnlyList<string> PickAudioFiles() => AudioPaths;
        public string? FilePath { get; set; }
        public string? FolderPath { get; set; }
        public string? PickPlaylistFile() => FilePath;
        public string? PickAudioFile() => null;
        public string? PickMusicFolder() => FolderPath;
    }
    private sealed class Player : IAudioPlayer
    {
        public float Volume { get; set; } = 1f;
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMinutes(2);
        public int PlayCount { get; private set; }
        public void Load(string path) { }
        public void Play() => PlayCount++;
        public void Pause() { }
        public void Stop() { }
        public void Seek(TimeSpan position) { }
    }
    private sealed class DeferredImporter : IPlaylistImportService
    {
        public Task<PlaylistImportResult> ImportTracksAsync(IReadOnlyList<string> paths, IProgress<PlaylistImportProgress>? progress = null) => ImportFileAsync("", progress);
        public TaskCompletionSource<PlaylistImportResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<PlaylistImportProgress>? Progress { get; private set; }
        public Task<PlaylistImportResult> ImportFileAsync(string path, IProgress<PlaylistImportProgress>? progress = null)
        {
            Progress = progress;
            return Completion.Task;
        }
        public Task<PlaylistImportResult> ImportFolderAsync(string path, IProgress<PlaylistImportProgress>? progress = null) => ImportFileAsync(path, progress);
    }
    private sealed class ProgressRecorder : IProgress<PlaylistImportProgress>
    {
        public List<PlaylistImportProgress> Values { get; } = [];
        public void Report(PlaylistImportProgress value) => Values.Add(value);
    }

    private sealed class SlowStore : IPlaylistStore, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<Playlist> Load() => [];
        public void Save(IEnumerable<Playlist> playlists)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Save was not released");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
