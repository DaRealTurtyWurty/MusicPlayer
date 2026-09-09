using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static class LibraryTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MusicPlayerLibraryTests", Guid.NewGuid().ToString("N"));
        var firstFolder = Path.Combine(directory, "First");
        var secondFolder = Path.Combine(directory, "Second");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);
        var a = Path.Combine(firstFolder, "A.mp3");
        var b = Path.Combine(firstFolder, "B.mp3");
        var c = Path.Combine(secondFolder, "C.mp3");
        var d = Path.Combine(secondFolder, "D.mp3");
        foreach (var path in new[] { a, b, c, d }) File.WriteAllBytes(path, []);
        var metadata = new Metadata();
        var picker = new Picker();
        var database = new SqliteMusicStore(Path.Combine(directory, "music.db"));
        ILibraryStore store = database;
        IPlaylistStore playlists = database;
        playlists.Save([new Playlist([metadata.ReadTrack(a), metadata.ReadTrack(a.ToUpperInvariant()), metadata.ReadTrack(b)])]);
        MainViewModel Create(ILibraryStore? library = null) => new(picker, picker, metadata,
            new LibraryScanner(metadata), new Player(), playlistStore: playlists, libraryStore: library ?? store);
        using (var vm = Create())
        {
            Check(vm.Tracks.Count == 2 && store.Load().Count == 2,
                "Existing playlists seed and persist a deduplicated library");
            vm.SelectedTrack = vm.Tracks[0];
            vm.PlaySelectedTrackCommand.Execute(null);
            vm.Queue.Add(vm.Tracks[1]);
            var current = vm.CurrentTrack;
            var queue = vm.Queue.ToArray();
            picker.Folder = secondFolder;
            await vm.AddMusicFolderCommand.ExecuteAsync(null);
            Check(vm.Tracks.Count == 4 && store.Load().Count == 4 && vm.Playlists.Count == 1,
                "Adding a folder extends the saved library without creating a playlist");
            picker.Folder = firstFolder;
            await vm.AddMusicFolderCommand.ExecuteAsync(null);
            Check(vm.Tracks.Count == 4 && vm.ToastMessage!.Contains("2 already"),
                "Overlapping folder imports preserve tracks and report duplicates");
            picker.Files = [c, Path.Combine(firstFolder, "..", "Second", "C.mp3"), Path.Combine(directory, "missing.mp3")];
            await vm.AddMusicFilesCommand.ExecuteAsync(null);
            Check(vm.Tracks.Count == 4 && vm.ToastMessage!.Contains("Skipped 1"),
                "File imports normalize paths, avoid duplicates, and report missing files");
            Check(vm.CurrentTrack == current && vm.Queue.SequenceEqual(queue) && vm.IsPlaying,
                "Library imports preserve playback and the queue");
            vm.ShowUncategorizedTracks = true;
            Check(vm.LibraryTracks.Cast<Track>().Select(t => t.Title).SequenceEqual(new[] { "C", "D" }),
                "Not in a playlist excludes tracks in any playlist");
            vm.LibrarySearchText = "D";
            Check(vm.LibraryTracks.Cast<Track>().Single().FilePath == d, "Search combines with playlist membership filtering");
            vm.PlayAllCommand.Execute(null);
            Check(vm.CurrentTrack!.FilePath == d && vm.Queue.Count == 0, "Play all uses the visible library results");
            vm.LibrarySearchText = "no match";
            Check(!vm.PlayAllCommand.CanExecute(null), "Play all is disabled for empty results");
            vm.ClearLibraryFiltersCommand.Execute(null);
            Check(vm.LibraryTracks.Count == 4 && !vm.ShowUncategorizedTracks && vm.LibrarySearchText == "",
                "Clear filters restores the entire library");
            var other = new Playlist([metadata.ReadTrack(c)]);
            vm.Playlists.Add(other);
            vm.ShowUncategorizedTracks = true;
            Check(vm.LibraryTracks.Cast<Track>().Single().FilePath == d, "Membership includes unselected playlists");
            other.Tracks.Clear();
            Check(vm.LibraryTracks.Count == 2 && vm.Tracks.Count == 4, "Removing playlist tracks immediately restores uncategorized results");
            vm.Playlists.Remove(other);
            vm.DeletePlaylistCommand.Execute(null);
            vm.ConfirmDeletePlaylistCommand.Execute(null);
            Check(vm.LibraryTracks.Count == 4 && vm.Tracks.Count == 4 && playlists.Load().Count == 0,
                "Deleting a playlist keeps songs separately added through Add music");
        }
        using (var restored = Create())
        {
            Check(restored.Tracks.Count == 4 && restored.Playlists.Count == 0,
                "Uncategorized music survives restarting after playlist deletion");
            picker.Folder = null;
            picker.Files = [];
            await restored.AddMusicFolderCommand.ExecuteAsync(null);
            await restored.AddMusicFilesCommand.ExecuteAsync(null);
            Check(restored.Tracks.Count == 4 && !restored.IsImportingPlaylist, "Canceling library pickers changes nothing");
            picker.Folder = Path.Combine(directory, "not-a-folder");
            await restored.AddMusicFolderCommand.ExecuteAsync(null);
            Check(restored.LibraryError is not null && restored.Tracks.Count == 4 && restored.AddMusicFilesCommand.CanExecute(null),
                "Failed folder imports report errors and restore import commands");
        }
        var corrupt = Path.Combine(directory, "corrupt.db");
        File.WriteAllText(corrupt, "invalid database");
        using (var damaged = Create(new SqliteMusicStore(corrupt)))
        {
            using var reader = new StreamReader(File.Open(corrupt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Check(damaged.LibraryError is not null && !damaged.AddMusicFilesCommand.CanExecute(null) && reader.ReadToEnd() == "invalid database",
                "Unreadable library databases are reported and preserved");
        }
        using (var adding = Create())
        {
            var extra = Path.Combine(directory, "E.mp3");
            File.WriteAllBytes(extra, []);
            picker.Files = [extra];
            adding.CreatePlaylistCommand.Execute(null);
            await adding.AddTracksToPlaylistCommand.ExecuteAsync(null);
            Check(adding.Tracks.Count == 5 && store.Load().Count == 5 && adding.SelectedPlaylist!.Tracks.Single().FilePath == extra,
                "Adding new files to an existing playlist also saves them in the library");
            adding.DeletePlaylistCommand.Execute(null);
            adding.ConfirmDeletePlaylistCommand.Execute(null);
        }
        var failingStore = new FailingStore();
        using (var failing = Create(failingStore))
        {
            picker.Files = [a];
            await failing.AddMusicFilesCommand.ExecuteAsync(null);
            Check(failing.Tracks.Count == 1 && failing.LibraryError!.Contains("could not be saved"),
                "Save failures retain session tracks and show a persistence error");
            failingStore.Fail = false;
            await failing.AddMusicFilesCommand.ExecuteAsync(null);
            Check(failing.LibraryError is null && failingStore.Saved.Single().FilePath == a && failing.Tracks.Count == 1,
                "Reimporting after a save failure retries persistence without duplicating songs");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class Metadata : IMetadataService
    {
        public Track ReadTrack(string path) => new() { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Duration = TimeSpan.FromMinutes(2) };
    }

    private sealed class Picker : IFilePickerService, IFolderPickerService
    {
        public string? Folder { get; set; }
        public IReadOnlyList<string> Files { get; set; } = [];
        public string? PickMusicFolder() => Folder;
        public IReadOnlyList<string> PickAudioFiles() => Files;
        public string? PickAudioFile() => null;
        public string? PickPlaylistFile() => null;
    }

    private sealed class FailingStore : ILibraryStore
    {
        public bool Fail { get; set; } = true;
        public Track[] Saved { get; private set; } = [];
        public IReadOnlyList<Track> Load() => [];
        public void Save(IEnumerable<Track> tracks)
        {
            if (Fail) throw new IOException("Disk unavailable");
            Saved = tracks.ToArray();
        }
    }

    private sealed class Player : IAudioPlayer
    {
        public float Volume { get; set; } = 1f;
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.FromMinutes(2);
        public void Load(string path) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(TimeSpan position) { }
    }
}
