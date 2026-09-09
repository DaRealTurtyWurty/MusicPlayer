using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;

internal static class SqlitePersistenceTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MusicPlayerSqliteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "music.db");
        var libraryPath = Path.Combine(directory, "library.json");
        var playlistsPath = Path.Combine(directory, "playlists.json");
        Track Track(string filename, string title = "Song") => new()
        {
            FilePath = Path.Combine(directory, filename), Title = title, Artist = "Artist",
            Album = "Album", Duration = TimeSpan.FromTicks(123456789)
        };
        var a = Track("café.mp3", "Library title");
        var b = Track("B.mp3");
        var standalone = Track("Standalone.mp3");
        var playlist = new Playlist([Track("CAFÉ.mp3", "Old title"), b, a]) { Name = "Favourites" };
        new JsonLibraryStore(libraryPath).Save([a, standalone, Track("sub/../café.mp3")]);
        new JsonPlaylistStore(playlistsPath).Save([playlist, new Playlist { Name = "Empty" }]);
        var originalLibrary = File.ReadAllBytes(libraryPath);
        var originalPlaylists = File.ReadAllBytes(playlistsPath);

        var store = new SqliteMusicStore(path);
        Check(store.LoadLibrary().Count == 3, "JSON migration merges library and playlists with normalized Unicode path deduplication");
        var restored = store.LoadPlaylists();
        Check(restored.Count == 2 && restored[0].Id == playlist.Id && restored[0].Name == playlist.Name && restored[1].Tracks.Count == 0,
            "SQLite migration preserves playlist IDs, names, gallery order and empty playlists");
        Check(restored[0].Tracks.Select(t => t.Title).SequenceEqual(new[] { "Library title", "Song", "Library title" }) &&
              restored[0].Tracks[0].Duration == a.Duration && restored[0].Tracks[0].Artist == a.Artist && restored[0].Tracks[0].Album == a.Album,
            "SQLite migration preserves track metadata, entry order and duplicates with library metadata taking precedence");
        Check(originalLibrary.SequenceEqual(File.ReadAllBytes(libraryPath)) && originalPlaylists.SequenceEqual(File.ReadAllBytes(playlistsPath)),
            "Legacy JSON files remain byte-for-byte unchanged as backups");
        using (var db = Context(path))
        {
            Check(!db.Database.HasPendingModelChanges() && db.Database.GetAppliedMigrations().Count() == 6,
                "EF migrations match the model and are recorded in the database");
            Check(db.Tracks.Count() == 3 && db.PlaylistEntries.Count() == 3,
                "Playlist entries reference shared track records");
            db.Database.ExecuteSqlRaw("CREATE TABLE WriteAudit (Kind TEXT NOT NULL)");
            db.Database.ExecuteSqlRaw("CREATE TRIGGER AuditTracks AFTER UPDATE ON Tracks BEGIN INSERT INTO WriteAudit VALUES ('Track'); END");
            db.Database.ExecuteSqlRaw("CREATE TRIGGER AuditEntries AFTER UPDATE ON PlaylistEntries BEGIN INSERT INTO WriteAudit VALUES ('Entry'); END");
            db.Database.ExecuteSqlRaw("CREATE TRIGGER AuditPlaylists AFTER UPDATE ON Playlists BEGIN INSERT INTO WriteAudit VALUES ('Playlist'); END");
        }

        store.SavePlaylists(restored);
        store.SaveLibrary(store.LoadLibrary());
        Check(AuditCount(path) == 0, "Unchanged saves issue no row updates");
        restored[0].Name = "Renamed";
        store.SavePlaylists(restored);
        Check(AuditCount(path) == 1, "Renaming a playlist updates only its playlist row");
        restored[0].Tracks.Move(1, 0);
        restored[0].Tracks.RemoveAt(2);
        store.SavePlaylists(restored);
        var restarted = new SqliteMusicStore(path);
        Check(restarted.LoadPlaylists()[0].Tracks.Select(t => t.Title).SequenceEqual(new[] { "Song", "Library title" }),
            "Reordered entries and removing one duplicate survive a new database context");

        // Fail after EF has inserted a new song, while it is inserting its playlist entry.
        using (var db = Context(path))
            db.Database.ExecuteSqlRaw("CREATE TRIGGER RejectEntry BEFORE INSERT ON PlaylistEntries BEGIN SELECT RAISE(ABORT, 'Injected write failure'); END");
        restored[0].Tracks.Add(Track("New.mp3"));
        restored[0].Name = "Must roll back";
        ExpectFailure(() => store.SavePlaylists(restored));
        Check(restarted.LoadLibrary().Count == 3 && restarted.LoadPlaylists()[0].Name == "Renamed" && restarted.LoadPlaylists()[0].Tracks.Count == 2,
            "Failed playlist saves roll back new tracks, playlist metadata and entries together");
        using (var db = Context(path)) db.Database.ExecuteSqlRaw("DROP TRIGGER RejectEntry");
        store.SavePlaylists(restored);
        Check(restarted.LoadLibrary().Count == 4 && restarted.LoadPlaylists()[0].Tracks.Count == 3,
            "Failed transactions can be retried with a fresh context");

        store.SavePlaylists([]);
        var afterDeletion = new SqliteMusicStore(path);
        Check(afterDeletion.LoadPlaylists().Count == 0 && afterDeletion.LoadLibrary().Count == 2 && afterDeletion.LoadKnownTracks().Count == 4,
            "Deleting playlists hides playlist-only songs without losing track records or resurrecting old JSON");
        store.SaveLibrary(Enumerable.Range(0, 1200).Select(i => ExplicitTrack($"Large/{i}.mp3")));
        Check(new SqliteMusicStore(path).LoadLibrary().Count == 1202, "Large imports use bounded track queries and retain explicit library music");
        Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.SaveLibrary([ExplicitTrack($"Concurrent/{i}.mp3")])))).GetAwaiter().GetResult();
        Check(store.LoadLibrary().Count == 1210, "Worker-thread saves use separate contexts and preserve every import");

        Track ExplicitTrack(string filename)
        {
            var track = Track(filename);
            track.ExplicitlyAddedToLibrary = true;
            return track;
        }

        CheckFailedMigration(directory, Track("Recovery.mp3"));
        CheckSessionStorage(directory, a, b);
        Check(new SqliteMusicStore(Path.Combine(directory, "fresh", "music.db")).LoadLibrary().Count == 0,
            "First launch without JSON creates an empty migrated database");
    }

    private static void CheckSessionStorage(string directory, Track a, Track b)
    {
        var path = Path.Combine(directory, "session", "music.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var db = Context(path))
        {
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().First());
            db.StorageStates.Add(new StorageState { Key = "LegacyJsonImported" });
            // Seed the old schema without asking the current EF model for newer columns.
            db.Database.ExecuteSqlInterpolated($"INSERT INTO Tracks (PathKey, FilePath, Title, DurationTicks) VALUES ({Path.GetFullPath(a.FilePath).ToUpperInvariant()}, {a.FilePath}, {a.Title}, 0)");
            db.SaveChanges();
        }
        var store = new SqliteMusicStore(path);
        Check(store.LoadSession().CurrentTrack is null && store.LoadLibrary().Single().FilePath == a.FilePath,
            "Existing SQLite databases gain playback tables without losing library data");
        store.SaveSession(new PlaybackSession(a, TimeSpan.FromSeconds(42.75), new[] { b, a, b })
        {
            History = [new(b, false), new(a, true), new(b, true)]
        });
        var restored = new SqliteMusicStore(path).LoadSession();
        Check(restored.CurrentTrack!.FilePath == a.FilePath && restored.Position.TotalSeconds == 42.75 &&
              restored.Queue.Select(t => t.FilePath).SequenceEqual(new[] { b.FilePath, a.FilePath, b.FilePath }),
            "SQLite restores current track, precise playback position, queue order and duplicates");
        Check(restored.History.Select(e => (e.Track.FilePath, e.Recycled)).SequenceEqual(new[]
            { (b.FilePath, false), (a.FilePath, true), (b.FilePath, true) }),
            "SQLite preserves duplicate history occurrences and their repeat-all state");
        using (var db = Context(path))
        {
            db.Database.ExecuteSqlRaw("CREATE TRIGGER RejectQueue BEFORE INSERT ON QueueEntries BEGIN SELECT RAISE(ABORT, 'Injected queue failure'); END");
            db.Database.ExecuteSqlRaw("CREATE TRIGGER RejectQueueUpdates BEFORE UPDATE ON QueueEntries BEGIN SELECT RAISE(ABORT, 'Unexpected queue update'); END");
        }
        store.SavePosition(TimeSpan.FromSeconds(50));
        Check(store.LoadSession().Position.TotalSeconds == 50, "Position checkpoints update the session without rewriting the queue");
        ExpectFailure(() => store.SaveSession(new PlaybackSession(b, TimeSpan.FromSeconds(12), new[] { b, a, b, a })));
        restored = store.LoadSession();
        Check(restored.CurrentTrack!.FilePath == a.FilePath && restored.Position.TotalSeconds == 50 && restored.Queue.Count == 3 &&
              restored.History.Count == 3,
            "Failed queue writes roll back history, current track and position along with queue changes");
        using (var db = Context(path))
        {
            db.Database.ExecuteSqlRaw("DROP TRIGGER RejectQueue");
            db.Database.ExecuteSqlRaw("DROP TRIGGER RejectQueueUpdates");
        }
        store.SaveSession(new PlaybackSession(null, TimeSpan.Zero, Array.Empty<Track>()));
        restored = new SqliteMusicStore(path).LoadSession();
        Check(restored.CurrentTrack is null && restored.Position == TimeSpan.Zero && restored.Queue.Count == 0 &&
              restored.History.Count == 0 && store.LoadLibrary().Count == 1 && store.LoadKnownTracks().Count == 2,
            "Playback-only songs do not become library members when saving or clearing a session");
    }

    private static void CheckFailedMigration(string root, Track track)
    {
        var directory = Path.Combine(root, "recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "music.db");
        var library = Path.Combine(directory, "library.json");
        var playlists = Path.Combine(directory, "playlists.json");
        new JsonLibraryStore(library).Save([track]);
        File.WriteAllText(playlists, "broken JSON");
        var store = new SqliteMusicStore(path);
        ExpectFailure(() => store.LoadLibrary());
        ExpectFailure(() => store.SaveLibrary([track]));
        using (var db = Context(path))
            Check(db.Tracks.Count() == 0 && db.StorageStates.Count() == 0 && File.ReadAllText(playlists) == "broken JSON",
                "Corrupt legacy JSON blocks writes without partially importing data or marking import complete");
        new JsonPlaylistStore(playlists).Save([new Playlist([track])]);
        using (var db = Context(path))
            db.Database.ExecuteSqlRaw("CREATE TRIGGER RejectMigration BEFORE INSERT ON PlaylistEntries BEGIN SELECT RAISE(ABORT, 'Injected migration failure'); END");
        ExpectFailure(() => store.LoadLibrary());
        using (var db = Context(path))
        {
            Check(db.Tracks.Count() == 0 && db.Playlists.Count() == 0 && db.StorageStates.Count() == 0,
                "Database write failure rolls back the entire legacy import including its completion marker");
            db.Database.ExecuteSqlRaw("DROP TRIGGER RejectMigration");
        }
        Check(store.LoadLibrary().Count == 1 && store.LoadPlaylists().Count == 1,
            "Legacy migration retries successfully after the cause of failure is repaired");
    }

    private static MusicDbContext Context(string path) => new(new DbContextOptionsBuilder<MusicDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()).Options);

    private static int AuditCount(string path)
    {
        using var db = Context(path);
        return db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM WriteAudit").Single();
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or DbUpdateException) { return; }
        throw new InvalidOperationException("Expected persistence to fail.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
}
