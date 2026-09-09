using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Models;
using MusicPlayer.Services.Persistence;

namespace MusicPlayer.Services;

/// <summary>
/// Shared library/playlist storage. Each operation owns its context and transaction;
/// UI and import worker threads never share an EF change tracker.
/// </summary>
public sealed class SqliteMusicStore : ILibraryStore, IPlaylistStore, IPlaybackSessionStore, ILibraryMaintenanceStore, ILibraryMembershipStore, IReleaseTypeStore
{
    private const string LegacyImportKey = "LegacyJsonImported";
    private readonly object _gate = new();
    private readonly string _databasePath;
    private readonly string _legacyDirectory;
    private readonly DbContextOptions<MusicDbContext> _options;
    private bool _initialized;

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "music.db");

    public SqliteMusicStore(string? databasePath = null, string? legacyDirectory = null)
    {
        _databasePath = Path.GetFullPath(databasePath ?? DefaultDatabasePath);
        _legacyDirectory = legacyDirectory ?? Path.GetDirectoryName(_databasePath)!;
        _options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath, ForeignKeys = true, DefaultTimeout = 30
            }.ToString()).Options;
    }

    IReadOnlyList<Track> ILibraryStore.Load() => LoadLibrary();
    IReadOnlyList<Playlist> IPlaylistStore.Load() => LoadPlaylists();
    void ILibraryStore.Save(IEnumerable<Track> tracks) => SaveLibrary(tracks);
    void IPlaylistStore.Save(IEnumerable<Playlist> playlists) => SavePlaylists(playlists);

    public IReadOnlyList<Track> LoadLibrary()
    {
        lock (_gate)
        {
            using var db = Open();
            return db.Tracks.AsNoTracking()
                .Where(t => t.ExplicitlyAddedToLibrary || db.PlaylistEntries.Any(e => e.TrackId == t.Id))
                .OrderBy(t => t.Id).AsEnumerable().Select(ToTrack).ToArray();
        }
    }

    public IReadOnlyDictionary<string, ReleaseType> LoadReleaseTypes()
    {
        lock (_gate)
        {
            using var db = Open();
            return db.ReleaseTypes.AsNoTracking().AsEnumerable()
                .Where(r => Enum.IsDefined(r.Type)).ToDictionary(r => r.ReleaseKey, r => r.Type, StringComparer.Ordinal);
        }
    }

    public void SaveReleaseType(string releaseKey, ReleaseType? type)
    {
        if (type is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(type));
        lock (_gate)
        {
            using var db = Open();
            var row = db.ReleaseTypes.Find(releaseKey);
            if (type is null)
            {
                if (row is not null) db.ReleaseTypes.Remove(row);
            }
            else if (row is null) db.ReleaseTypes.Add(new StoredReleaseType { ReleaseKey = releaseKey, Type = type.Value });
            else row.Type = type.Value;
            db.SaveChanges();
        }
    }

    public IReadOnlyList<Playlist> LoadPlaylists()
    {
        lock (_gate)
        {
            using var db = Open();
            return db.Playlists.AsNoTracking().Include(p => p.Entries).ThenInclude(e => e.Track)
                .OrderBy(p => p.Position).AsEnumerable()
                .Select(p => new Playlist(p.Entries.OrderBy(e => e.Position).Select(e => ToTrack(e.Track)))
                { Id = p.Id, Name = p.Name }).ToArray();
        }
    }

    public IReadOnlyList<Track> LoadKnownTracks()
    {
        lock (_gate)
        {
            using var db = Open();
            return db.Tracks.AsNoTracking().OrderBy(t => t.Id).AsEnumerable().Select(ToTrack).ToArray();
        }
    }

    // Membership is explicit addition OR a playlist reference. Other records stay available to playback.
    public void SaveLibrary(IEnumerable<Track> tracks)
    {
        var snapshot = tracks.ToArray();
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            UpsertTracks(db, snapshot);
            db.SaveChanges();
            transaction.Commit();
        }
    }

    public void SavePlaylists(IEnumerable<Playlist> playlists)
    {
        var snapshot = playlists.Select(p => new Playlist(p.Tracks) { Id = p.Id, Name = p.Name }).ToArray();
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            SynchronizePlaylists(db, snapshot);
            db.SaveChanges();
            transaction.Commit();
        }
    }

    public void DeletePlaylist(Guid playlistId, bool keepExclusiveSongs)
    {
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            var playlist = db.Playlists.SingleOrDefault(p => p.Id == playlistId);
            if (playlist is null) return;
            if (keepExclusiveSongs)
                db.Tracks.Where(t => !t.ExplicitlyAddedToLibrary &&
                        db.PlaylistEntries.Any(e => e.PlaylistId == playlistId && e.TrackId == t.Id) &&
                        !db.PlaylistEntries.Any(e => e.PlaylistId != playlistId && e.TrackId == t.Id))
                    .ExecuteUpdate(s => s.SetProperty(t => t.ExplicitlyAddedToLibrary, true));
            db.Playlists.Remove(playlist);
            db.SaveChanges();
            transaction.Commit();
        }
    }

    public PlaybackSession LoadSession()
    {
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            var session = db.PlaybackSessions.AsNoTracking().Include(s => s.CurrentTrack).SingleOrDefault();
            var queue = db.QueueEntries.AsNoTracking().Include(e => e.Track).OrderBy(e => e.Position)
                .AsEnumerable().Select(e => ToTrack(e.Track)).ToArray();
            var history = db.PlaybackHistoryEntries.AsNoTracking().Include(e => e.Track).OrderBy(e => e.Position)
                .AsEnumerable().Select(e => new PlaybackHistoryEntry(ToTrack(e.Track), e.Recycled)).ToArray();
            return new PlaybackSession(session?.CurrentTrack is { } current ? ToTrack(current) : null,
                TimeSpan.FromTicks(session?.PositionTicks ?? 0), queue) { History = history };
        }
    }

    public void SaveSession(PlaybackSession session)
    {
        if (session.Position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(session));
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            var tracks = UpsertTracks(db, (session.CurrentTrack is { } current
                ? session.Queue.Prepend(current) : session.Queue).Concat(session.History.Select(e => e.Track)));
            var stored = db.PlaybackSessions.SingleOrDefault();
            if (stored is null)
            {
                stored = new StoredPlaybackSession();
                db.PlaybackSessions.Add(stored);
            }
            stored.CurrentTrack = session.CurrentTrack is { } track ? tracks[TrackKey(track)] : null;
            if (session.CurrentTrack is null) stored.CurrentTrackId = null;
            stored.PositionTicks = session.CurrentTrack is null ? 0 : session.Position.Ticks;
            var entries = db.QueueEntries.ToDictionary(e => e.Position);
            db.QueueEntries.RemoveRange(entries.Values.Where(e => e.Position >= session.Queue.Count));
            for (var index = 0; index < session.Queue.Count; index++)
            {
                var queuedTrack = tracks[TrackKey(session.Queue[index])];
                if (entries.TryGetValue(index, out var entry)) entry.Track = queuedTrack;
                else db.QueueEntries.Add(new StoredQueueEntry { Position = index, Track = queuedTrack });
            }
            var historyEntries = db.PlaybackHistoryEntries.ToDictionary(e => e.Position);
            db.PlaybackHistoryEntries.RemoveRange(historyEntries.Values.Where(e => e.Position >= session.History.Count));
            for (var index = 0; index < session.History.Count; index++)
            {
                var history = session.History[index];
                if (!historyEntries.TryGetValue(index, out var entry))
                {
                    entry = new StoredPlaybackHistoryEntry { Position = index };
                    db.PlaybackHistoryEntries.Add(entry);
                }
                entry.Track = tracks[TrackKey(history.Track)];
                entry.Recycled = history.Recycled;
            }
            db.SaveChanges();
            transaction.Commit();
        }
    }

    public void SavePosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            using var db = Open();
            db.PlaybackSessions.Where(s => s.Id == 1 && s.CurrentTrackId != null)
                .ExecuteUpdate(setters => setters.SetProperty(s => s.PositionTicks, position.Ticks));
        }
    }

    public IReadOnlyList<WatchedMusicFolder> LoadMusicFolders()
    {
        lock (_gate)
        {
            using var db = Open();
            return db.MusicFolders.AsNoTracking().Select(f => new WatchedMusicFolder(f.Path, f.IncludeSubdirectories, f.DiscoverNewTracks)).ToArray();
        }
    }

    public void SaveMusicFolder(WatchedMusicFolder folder)
    {
        var path = Path.GetFullPath(folder.Path);
        var key = path.ToUpperInvariant();
        lock (_gate)
        {
            using var db = Open();
            var stored = db.MusicFolders.Find(key);
            if (stored is null) db.MusicFolders.Add(new StoredMusicFolder
                { PathKey = key, Path = path, IncludeSubdirectories = folder.IncludeSubdirectories, DiscoverNewTracks = folder.DiscoverNewTracks });
            else
            {
                stored.IncludeSubdirectories |= folder.IncludeSubdirectories;
                stored.DiscoverNewTracks |= folder.DiscoverNewTracks;
            }
            db.SaveChanges();
        }
    }

    public void RelocateTrack(string originalPath, Track replacement)
    {
        var oldKey = Path.GetFullPath(originalPath).ToUpperInvariant();
        var newKey = TrackKey(replacement);
        lock (_gate)
        {
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            var original = db.Tracks.SingleOrDefault(t => t.PathKey == oldKey);
            if (original is null)
                throw new InvalidOperationException("The original track is no longer in the library.");
            var duplicate = db.Tracks.SingleOrDefault(t => t.PathKey == newKey && t.Id != original.Id);
            if (duplicate is not null)
            {
                original.ExplicitlyAddedToLibrary |= duplicate.ExplicitlyAddedToLibrary;
                // Keep the original identity and merge any already-imported destination references.
                db.PlaylistEntries.Where(e => e.TrackId == duplicate.Id).ExecuteUpdate(s => s.SetProperty(e => e.TrackId, original.Id));
                db.QueueEntries.Where(e => e.TrackId == duplicate.Id).ExecuteUpdate(s => s.SetProperty(e => e.TrackId, original.Id));
                db.PlaybackHistoryEntries.Where(e => e.TrackId == duplicate.Id).ExecuteUpdate(s => s.SetProperty(e => e.TrackId, original.Id));
                db.PlaybackSessions.Where(s => s.CurrentTrackId == duplicate.Id).ExecuteUpdate(s => s.SetProperty(e => e.CurrentTrackId, (long?)original.Id));
                db.Tracks.Remove(duplicate);
                db.SaveChanges();
            }
            original.PathKey = newKey;
            original.FilePath = Path.GetFullPath(replacement.FilePath);
            UpdateTrackMetadata(original, replacement);
            db.SaveChanges();
            transaction.Commit();
        }
    }

    private MusicDbContext Open()
    {
        var db = new MusicDbContext(_options);
        try
        {
            if (!_initialized)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                db.Database.Migrate();
                ImportLegacyJson(db);
                _initialized = true;
            }
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void ImportLegacyJson(MusicDbContext db)
    {
        using var transaction = db.Database.BeginTransaction();
        if (db.StorageStates.Any(s => s.Key == LegacyImportKey)) return;
        if (db.Tracks.Any() || db.Playlists.Any())
            throw new InvalidDataException("The database contains music but has no JSON import record. Restore a database backup before continuing.");

        // Read both sources before writing any music. Keep the originals untouched as backups.
        // A failed import leaves the marker absent, so it can be retried after repairing the JSON.
        var library = new JsonLibraryStore(Path.Combine(_legacyDirectory, "library.json")).Load();
        var playlists = new JsonPlaylistStore(Path.Combine(_legacyDirectory, "playlists.json")).Load();
        SynchronizePlaylists(db, playlists);
        // The dedicated library takes precedence if legacy copies contain different metadata.
        foreach (var track in library) track.ExplicitlyAddedToLibrary = true;
        UpsertTracks(db, library);
        db.StorageStates.Add(new StorageState { Key = LegacyImportKey });
        db.SaveChanges();
        transaction.Commit();
    }

    private static Dictionary<string, StoredTrack> UpsertTracks(MusicDbContext db, IEnumerable<Track> tracks)
    {
        var incoming = tracks.Select(t => (Key: TrackKey(t), Track: t)).DistinctBy(t => t.Key).ToArray();
        var stored = new Dictionary<string, StoredTrack>(StringComparer.Ordinal);
        // Bounded queries avoid SQLite's parameter limit for large imports.
        foreach (var batch in incoming.Chunk(500))
        {
            var keys = batch.Select(t => t.Key).ToArray();
            foreach (var track in db.Tracks.Where(t => keys.Contains(t.PathKey))) stored[track.PathKey] = track;
        }
        foreach (var track in db.Tracks.Local) stored[track.PathKey] = track;
        foreach (var (key, track) in incoming)
        {
            if (!stored.TryGetValue(key, out var row))
            {
                row = new StoredTrack { PathKey = key, FilePath = track.FilePath, Title = track.Title };
                db.Tracks.Add(row);
                stored.Add(key, row);
            }
            UpdateTrackMetadata(row, track);
        }
        return stored;
    }

    private static void UpdateTrackMetadata(StoredTrack row, Track track)
    {
        row.Title = track.Title;
        row.Artist = track.Artist;
        row.Album = track.Album;
        row.ReleaseTypeTag = track.ReleaseTypeTag;
        row.DurationTicks = track.Duration.Ticks;
        row.FileSize = track.FileSize;
        row.LastWriteTimeUtcTicks = track.LastWriteTimeUtcTicks;
        row.IsMissing = track.IsMissing;
        // Metadata or session snapshots must never undo an explicit addition.
        row.ExplicitlyAddedToLibrary |= track.ExplicitlyAddedToLibrary;
    }

    private static void SynchronizePlaylists(MusicDbContext db, IReadOnlyList<Playlist> playlists)
    {
        if (playlists.Any(p => string.IsNullOrWhiteSpace(p.Name)) || playlists.Select(p => p.Id).Distinct().Count() != playlists.Count)
            throw new InvalidDataException("Playlists must have names and unique IDs.");

        // New songs and their playlist entries commit in the same transaction.
        var tracks = UpsertTracks(db, playlists.SelectMany(p => p.Tracks));
        var stored = db.Playlists.Include(p => p.Entries).ToDictionary(p => p.Id);
        var retained = playlists.Select(p => p.Id).ToHashSet();
        db.Playlists.RemoveRange(stored.Values.Where(p => !retained.Contains(p.Id)));
        for (var position = 0; position < playlists.Count; position++)
        {
            var playlist = playlists[position];
            if (!stored.TryGetValue(playlist.Id, out var row))
            {
                row = new StoredPlaylist { Id = playlist.Id, Name = playlist.Name };
                db.Playlists.Add(row);
            }
            row.Name = playlist.Name;
            row.Position = position;
            var entries = row.Entries.ToDictionary(e => e.Position);
            db.PlaylistEntries.RemoveRange(row.Entries.Where(e => e.Position >= playlist.Tracks.Count));
            for (var index = 0; index < playlist.Tracks.Count; index++)
            {
                var track = tracks[TrackKey(playlist.Tracks[index])];
                if (entries.TryGetValue(index, out var entry)) entry.Track = track;
                else row.Entries.Add(new StoredPlaylistEntry { PlaylistId = row.Id, Position = index, Track = track });
            }
        }
        // EF compares tracked values and writes only changed rows, preserving unchanged entries.
    }

    private static string TrackKey(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || track.Duration.Ticks < 0 || track.Title is null)
            throw new InvalidDataException("A saved track is invalid.");
        return Path.GetFullPath(track.FilePath).ToUpperInvariant();
    }

    private static Track ToTrack(StoredTrack track) => new()
    {
        FilePath = track.FilePath, Title = track.Title, Artist = track.Artist,
        Album = track.Album, Duration = TimeSpan.FromTicks(track.DurationTicks),
        ReleaseTypeTag = track.ReleaseTypeTag,
        FileSize = track.FileSize, LastWriteTimeUtcTicks = track.LastWriteTimeUtcTicks, IsMissing = track.IsMissing,
        ExplicitlyAddedToLibrary = track.ExplicitlyAddedToLibrary
    };
}
