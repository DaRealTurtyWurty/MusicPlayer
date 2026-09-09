using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;

internal static class LibraryRefreshTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MusicPlayerRefreshTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var music = Path.Combine(root, "music");
        Directory.CreateDirectory(music);
        var first = Path.Combine(music, "one.mp3");
        var second = Path.Combine(music, "two.mp3");
        File.WriteAllText(first, "initial");
        File.WriteAllText(second, "initial");
        var metadata = new RefreshMetadata();
        metadata.Tags[first] = ("Song", "Artist");
        metadata.Tags[second] = ("Other", "Artist");
        var service = new LibraryRefreshService(metadata);
        var folders = new[] { new WatchedMusicFolder(music, true) };
        var initial = await service.RefreshAsync([], folders, false, default);
        Check(initial.Added.Count == 2 && initial.Added.All(t => t.FileSize == 7 && t.LastWriteTimeUtcTicks.HasValue),
            "Discovery stores each file's size and modification time alongside metadata");
        var reads = metadata.Reads;
        var unchanged = await service.RefreshAsync(initial.Added, folders, false, default);
        Check(unchanged.Updates.Count == 0 && unchanged.Added.Count == 0 && metadata.Reads == reads,
            "Incremental refresh skips metadata reads for unchanged tracks and overlapping discovery");
        metadata.Tags[first] = ("Edited title", "Edited artist");
        File.AppendAllText(first, " changed tags");
        var changed = await service.RefreshAsync(initial.Added, folders, false, default);
        Check(changed.Updates.Single().Title == "Edited title" && metadata.Reads == reads + 1,
            "Only changed files have their metadata reread");
        var updated = new[] { changed.Updates.Single(), initial.Added.Single(t => t.FilePath == second) };
        reads = metadata.Reads;
        var forced = await service.RefreshAsync(updated, folders, true, default);
        Check(forced.Updates.Count == 2 && metadata.Reads == reads + 2, "Manual rescan rereads tags even when file stamps match");
        File.Move(first, first + ".offline");
        var missing = await service.RefreshAsync(updated, folders, false, default);
        var unavailable = missing.Updates.Single();
        Check(unavailable.IsMissing && unavailable.Title == "Edited title" && unavailable.Artist == "Edited artist" &&
              unavailable.Duration == updated[0].Duration, "Missing files retain their metadata and become unavailable");
        File.Move(first + ".offline", first);
        var recovered = await service.RefreshAsync([unavailable, updated[1]], folders, false, default);
        Check(recovered.Updates.Single().IsMissing == false, "Files that return become available automatically");
        metadata.Broken.Add(first);
        File.AppendAllText(first, " partial edit");
        var corrupt = await service.RefreshAsync(updated, folders, false, default);
        Check(corrupt.Failed == 1 && corrupt.Updates.Count == 0,
            "Unreadable tags preserve prior metadata and leave the old fingerprint for retry");
        metadata.Broken.Clear();
        metadata.Tags[first] = ("  MY   Song ", "THE artist");
        metadata.Tags[second] = ("my song", "Wrong artist");
        var nested = Path.Combine(music, "nested");
        Directory.CreateDirectory(nested);
        var duplicate = Path.Combine(nested, "different-filename.flac");
        File.WriteAllText(duplicate, "copy");
        metadata.Tags[duplicate] = ("my song", "the artist");
        var target = new Track { FilePath = "missing", Title = "My Song", Artist = "The Artist" };
        var matches = await service.FindMatchesAsync(target, music, default);
        Check(matches.Matches.Count == 2 && matches.Matches.Any(t => t.FilePath == duplicate) &&
              matches.Matches.All(t => t.FilePath != second),
            "Locate searches subfolders by normalized title AND artist, returning all ambiguous matches");
        var absentArtist = await service.FindMatchesAsync(new Track { FilePath = "missing", Title = "My Song" }, music, default);
        Check(absentArtist.Matches.Count == 0, "Folder locate never guesses when saved artist metadata is absent");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await service.RefreshAsync(updated, folders, false, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(true, "Background scans honor cancellation"); }
        }
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var monitor = new LibraryFileMonitor(() => notified.TrySetResult()))
        {
            monitor.Configure(folders);
            File.AppendAllText(duplicate, " watcher event");
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "Folder monitoring detects edits in nested music folders");
        }
        CheckPersistence(root, updated[0], updated[1]);
    }

    private static void CheckPersistence(string root, Track original, Track other)
    {
        var path = Path.Combine(root, "music.db");
        MusicDbContext Context() => new(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={path};Foreign Keys=True").Options);
        using (var db = Context()) db.GetService<IMigrator>().Migrate("20260908105456_PersistQueueHistory");
        var store = new SqliteMusicStore(path);
        store.SaveMusicFolder(new WatchedMusicFolder(Path.GetDirectoryName(original.FilePath)!, true));
        Check(store.LoadMusicFolders().Single().IncludeSubdirectories, "Watched folder settings persist across restarts");
        store.SaveLibrary([original.WithFileState(original.FileSize, original.LastWriteTimeUtcTicks, true), other]);
        var playlist = new Playlist([original, original, other]);
        store.SavePlaylists([playlist]);
        store.SaveSession(new PlaybackSession(original, TimeSpan.FromSeconds(17), [original, other, original])
        { History = [new(original, false), new(other, true)] });
        long id;
        using (var db = Context())
        {
            id = db.Tracks.Single(t => t.FilePath == original.FilePath).Id;
            Check(!db.Database.HasPendingModelChanges() && !db.Database.GetPendingMigrations().Any(),
                "Existing history databases migrate to refresh metadata without losing their schema");
        }
        var replacement = new Track { FilePath = other.FilePath, Title = "Relocated", Artist = "Artist", FileSize = 999,
            LastWriteTimeUtcTicks = 12345, Duration = original.Duration };
        store.RelocateTrack(original.FilePath, replacement);
        using (var db = Context()) Check(db.Tracks.Single().Id == id && db.Tracks.Single().FileSize == 999,
            "Locate preserves the original track ID and merges an already-imported destination");
        Check(store.LoadPlaylists().Single().Tracks.Count == 3 && store.LoadPlaylists().Single().Tracks.All(t => t.FilePath == other.FilePath) &&
              store.LoadSession().Queue.Count == 3 && store.LoadSession().History.Count == 2 &&
              store.LoadSession().CurrentTrack!.FilePath == other.FilePath && store.LoadSession().Position.TotalSeconds == 17,
            "Relinking updates every playlist, queue, history and current reference while preserving duplicates and position");
        var oldPath = Path.Combine(root, "another-missing.mp3");
        store.SaveLibrary([new Track { FilePath = oldPath, Title = "Keep me", IsMissing = true, ExplicitlyAddedToLibrary = true }]);
        using (var db = Context()) db.Database.ExecuteSqlRaw(
            "CREATE TRIGGER RejectRelink BEFORE UPDATE ON Tracks BEGIN SELECT RAISE(ABORT, 'Injected relink failure'); END");
        try { store.RelocateTrack(oldPath, replacement); throw new Exception("Expected relink failure"); }
        catch (DbUpdateException) { }
        Check(store.LoadLibrary().Count == 2 && store.LoadLibrary().Any(t => t.FilePath == oldPath && t.IsMissing) &&
              store.LoadSession().CurrentTrack!.FilePath == other.FilePath,
            "Failed relinking rolls back destination merging and keeps the original library and playback references");
    }

    internal sealed class RefreshMetadata : IMetadataService
    {
        public Dictionary<string, (string Title, string Artist)> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Reads { get; private set; }
        public Track ReadTrack(string path)
        {
            Reads++;
            if (Broken.Contains(path)) throw new IOException("Tags are being edited");
            var tags = Tags.TryGetValue(path, out var value) ? value : (Path.GetFileNameWithoutExtension(path), "Artist");
            return new Track { FilePath = path, Title = tags.Item1, Artist = tags.Item2, Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
}
