using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckLibraryMembershipAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MusicPlayerMembershipTests", Guid.NewGuid().ToString("N"));
        var music = Path.Combine(root, "music");
        Directory.CreateDirectory(music);
        string Song(string name)
        {
            var path = Path.Combine(music, name + ".mp3");
            File.WriteAllText(path, "audio fixture");
            return path;
        }
        var only = Song("Playlist only");
        var shared = Song("Shared");
        var direct = Song("Direct addition");
        var metadata = new LibraryRefreshTests.RefreshMetadata();
        var picker = new MembershipPicker();
        var database = Path.Combine(root, "music.db");
        var store = new SqliteMusicStore(database);
        var service = new LibraryRefreshService(metadata);
        MainViewModel Create() => new(picker, picker, metadata, new Scanner(), new FakePlayer(), playlistStore: store,
            libraryStore: store, playbackSessionStore: store, libraryRefreshService: service, monitorLibrary: false);
        async Task<Playlist> Import(MainViewModel vm, string name, params string[] paths)
        {
            picker.PlaylistFile = Path.Combine(root, name + ".m3u");
            File.WriteAllLines(picker.PlaylistFile, paths);
            await vm.ImportPlaylistFileCommand.ExecuteAsync(null);
            return vm.SelectedPlaylist!;
        }
        using (var vm = Create())
        {
            var first = await Import(vm, "First playlist", only, only, shared, direct);
            var second = await Import(vm, "Second playlist", shared);
            Check(vm.Tracks.Count == 3 && store.LoadLibrary().All(t => !t.ExplicitlyAddedToLibrary),
                "Playlist imports expose songs in the library without marking them as direct additions");
            picker.Files = [direct];
            await vm.AddMusicFilesCommand.ExecuteAsync(null);
            Check(vm.Tracks.Count == 3 && store.LoadLibrary().Single(t => t.FilePath == direct).ExplicitlyAddedToLibrary &&
                  !store.LoadLibrary().Single(t => t.FilePath == only).ExplicitlyAddedToLibrary,
                "Adding an already-imported song through Add music upgrades only that song's explicit membership");
            vm.SelectedTrack = vm.Tracks.Single(t => t.FilePath == only);
            vm.PlaySelectedTrackCommand.Execute(null);
            vm.SelectedTrack = vm.Tracks.Single(t => t.FilePath == shared);
            vm.PlaySelectedTrackCommand.Execute(null);
            vm.Queue.Add(first.Tracks[0]); vm.Queue.Add(first.Tracks[3]); vm.Queue.Add(first.Tracks[1]);
            vm.OpenPlaylistCommand.Execute(first);
            vm.DeletePlaylistCommand.Execute(null);
            Check(vm.PlaylistExclusiveSongCount == 1 && vm.DeletePlaylistLibraryImpact.StartsWith("1 song will also leave") &&
                  !vm.KeepPlaylistSongsInLibrary, "Deletion impact counts unique exclusive songs, excluding shared and direct additions");
            vm.KeepPlaylistSongsInLibrary = true;
            Check(vm.DeletePlaylistLibraryImpact.Contains("kept"), "Keep option updates the deletion impact preview");
            vm.CancelDeletePlaylistCommand.Execute(null);
            Check(!vm.KeepPlaylistSongsInLibrary && vm.Tracks.Count == 3 &&
                  !store.LoadKnownTracks().Single(t => t.FilePath == only).ExplicitlyAddedToLibrary,
                "Cancelling does not promote songs and resets the Keep option");
            vm.DeletePlaylistCommand.Execute(null);
            vm.ConfirmDeletePlaylistCommand.Execute(null);
            Check(vm.Tracks.Select(t => t.FilePath).ToHashSet().SetEquals([shared, direct]) && store.LoadLibrary().Count == 2 &&
                  vm.Playlists.Single() == second && vm.CurrentTrack!.FilePath == shared && vm.IsPlaying &&
                  vm.Queue.Count == 3 && vm.QueueTimeline.First().Track.FilePath == only && File.Exists(only),
                "Deleting a playlist removes only exclusive library members while keeping files, playback, queue duplicates and history");
            await vm.RefreshLibraryAsync(force: true);
            Check(vm.Tracks.Count == 2 && !store.LoadLibrary().Any(t => t.FilePath == only) && store.LoadKnownTracks().Count == 3,
                "A forced rescan does not rediscover removed playlist-only songs in a shared watched folder");
        }
        using (var vm = Create())
        {
            Check(vm.Tracks.Count == 2 && vm.Queue.Count == 3 && vm.QueueTimeline.First().Track.FilePath == only,
                "Library cleanup and independent queue/history references survive restart");
            await vm.RefreshLibraryAsync();
            Check(vm.Tracks.Count == 2, "Startup-style refresh keeps historical track records out of the library");
            picker.Files = [only];
            await vm.AddMusicFilesCommand.ExecuteAsync(null);
            Check(vm.Tracks.Count == 3 && store.LoadLibrary().Single(t => t.FilePath == only).ExplicitlyAddedToLibrary,
                "Explicitly adding a removed song restores its library membership without duplicating its record");
            var keepPath = Song("Keep this song");
            var keep = await Import(vm, "Keep playlist", keepPath, keepPath);
            vm.DeletePlaylistCommand.Execute(null);
            vm.KeepPlaylistSongsInLibrary = true;
            vm.ConfirmDeletePlaylistCommand.Execute(null);
            Check(!vm.Playlists.Contains(keep) && vm.Tracks.Single(t => t.FilePath == keepPath).ExplicitlyAddedToLibrary &&
                  store.LoadLibrary().Single(t => t.FilePath == keepPath).ExplicitlyAddedToLibrary,
                "Keep these songs promotes exclusive tracks and deletes the playlist in one operation");
            var failedPath = Song("Rollback song");
            var failed = await Import(vm, "Fail deletion", failedPath);
            using (var db = MembershipContext(database)) db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectPlaylistDelete BEFORE DELETE ON Playlists BEGIN SELECT RAISE(ABORT, 'Injected deletion failure'); END");
            vm.DeletePlaylistCommand.Execute(null);
            vm.KeepPlaylistSongsInLibrary = true;
            vm.ConfirmDeletePlaylistCommand.Execute(null);
            Check(vm.IsDeletePlaylistConfirmationOpen && vm.PlaylistDeletionError is not null && vm.Playlists.Contains(failed) &&
                  store.LoadPlaylists().Any(p => p.Id == failed.Id) &&
                  !store.LoadKnownTracks().Single(t => t.FilePath == failedPath).ExplicitlyAddedToLibrary &&
                  !vm.Tracks.Single(t => t.FilePath == failedPath).ExplicitlyAddedToLibrary,
                "Failed deletion rolls back Keep promotion and leaves both saved and visible playlist state intact");
            using (var db = MembershipContext(database)) db.Database.ExecuteSqlRaw("DROP TRIGGER RejectPlaylistDelete");
            vm.KeepPlaylistSongsInLibrary = false;
            vm.ConfirmDeletePlaylistCommand.Execute(null);
            Check(!vm.IsDeletePlaylistConfirmationOpen && !vm.Tracks.Any(t => t.FilePath == failedPath),
                "A failed playlist deletion can be retried with the latest Keep selection");
        }
        var refreshed = store.LoadKnownTracks();
        var scan = await service.RefreshAsync(refreshed, [new WatchedMusicFolder(music, true, false)], false, default);
        Check(scan.Added.Count == 0, "Folders watched only for playlist tracks do not add independent library discoveries");
        CheckMembershipMigration(root);
    }

    private static MusicDbContext MembershipContext(string path) => new(new DbContextOptionsBuilder<MusicDbContext>()
        .UseSqlite($"Data Source={path};Foreign Keys=True").Options);

    private static void CheckMembershipMigration(string root)
    {
        var path = Path.Combine(root, "upgrade.db");
        var store = new SqliteMusicStore(path);
        var member = new Track { FilePath = Path.Combine(root, "member.mp3"), Title = "Member", ExplicitlyAddedToLibrary = true };
        var standalone = new Track { FilePath = Path.Combine(root, "standalone.mp3"), Title = "Standalone", ExplicitlyAddedToLibrary = true };
        store.SaveLibrary([member, standalone]);
        store.SavePlaylists([new Playlist([member])]);
        store.SaveMusicFolder(new WatchedMusicFolder(root, true));
        using (var db = MembershipContext(path))
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().Single(m => m.EndsWith("_RefreshAndLocateTracks")));
        var migrated = new SqliteMusicStore(path);
        var tracks = migrated.LoadLibrary();
        Check(tracks.Count == 2 && !tracks.Single(t => t.FilePath == member.FilePath).ExplicitlyAddedToLibrary &&
              tracks.Single(t => t.FilePath == standalone.FilePath).ExplicitlyAddedToLibrary && migrated.LoadMusicFolders().Single().DiscoverNewTracks,
            "Migration classifies existing playlist members as playlist-sourced while preserving standalone songs and folder discovery");
    }

    private sealed class MembershipPicker : IFilePickerService, IFolderPickerService
    {
        public string? PlaylistFile { get; set; }
        public IReadOnlyList<string> Files { get; set; } = [];
        public string? PickPlaylistFile() => PlaylistFile;
        public IReadOnlyList<string> PickAudioFiles() => Files;
        public string? PickAudioFile() => null;
        public string? PickMusicFolder() => null;
    }
}
