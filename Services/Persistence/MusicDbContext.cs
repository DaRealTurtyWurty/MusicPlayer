using Microsoft.EntityFrameworkCore;

namespace MusicPlayer.Services.Persistence;

public sealed class MusicDbContext(DbContextOptions<MusicDbContext> options) : DbContext(options)
{
    public DbSet<StoredTrack> Tracks => Set<StoredTrack>();
    public DbSet<StoredReleaseType> ReleaseTypes => Set<StoredReleaseType>();
    public DbSet<StoredArtistIdentity> ArtistIdentities => Set<StoredArtistIdentity>();
    public DbSet<StoredMusicFolder> MusicFolders => Set<StoredMusicFolder>();
    public DbSet<StoredPlaylist> Playlists => Set<StoredPlaylist>();
    public DbSet<StoredPlaylistEntry> PlaylistEntries => Set<StoredPlaylistEntry>();
    public DbSet<StorageState> StorageStates => Set<StorageState>();
    public DbSet<StoredPlaybackSession> PlaybackSessions => Set<StoredPlaybackSession>();
    public DbSet<StoredQueueEntry> QueueEntries => Set<StoredQueueEntry>();
    public DbSet<StoredPlaybackHistoryEntry> PlaybackHistoryEntries => Set<StoredPlaybackHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredMusicFolder>().HasKey(f => f.PathKey);
        modelBuilder.Entity<StoredReleaseType>().HasKey(r => r.ReleaseKey);
        modelBuilder.Entity<StoredArtistIdentity>().HasKey(r => r.LookupKey);
        modelBuilder.Entity<StoredTrack>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.PathKey).IsUnique();
            entity.HasIndex(t => new { t.Artist, t.Album });
            entity.ToTable("Tracks", table => table.HasCheckConstraint("CK_Tracks_Duration", "DurationTicks >= 0"));
        });
        modelBuilder.Entity<StoredPlaylist>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.HasMany(p => p.Entries).WithOne().HasForeignKey(e => e.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StoredPlaylistEntry>(entity =>
        {
            // Position identifies an occurrence, allowing the same song more than once.
            entity.HasKey(e => new { e.PlaylistId, e.Position });
            entity.HasOne(e => e.Track).WithMany().HasForeignKey(e => e.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("PlaylistEntries", table => table.HasCheckConstraint("CK_PlaylistEntries_Position", "Position >= 0"));
        });
        modelBuilder.Entity<StorageState>().HasKey(s => s.Key);
        modelBuilder.Entity<StoredPlaybackSession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.HasOne(s => s.CurrentTrack).WithMany().HasForeignKey(s => s.CurrentTrackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("PlaybackSessions", table =>
            {
                table.HasCheckConstraint("CK_PlaybackSessions_Id", "Id = 1");
                table.HasCheckConstraint("CK_PlaybackSessions_Position", "PositionTicks >= 0");
            });
        });
        modelBuilder.Entity<StoredQueueEntry>(entity =>
        {
            entity.HasKey(e => e.Position);
            entity.Property(e => e.Position).ValueGeneratedNever();
            entity.HasOne(e => e.Track).WithMany().HasForeignKey(e => e.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("QueueEntries", table => table.HasCheckConstraint("CK_QueueEntries_Position", "Position >= 0"));
        });
        modelBuilder.Entity<StoredPlaybackHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Position);
            entity.Property(e => e.Position).ValueGeneratedNever();
            entity.HasOne(e => e.Track).WithMany().HasForeignKey(e => e.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("PlaybackHistoryEntries", table =>
                table.HasCheckConstraint("CK_PlaybackHistoryEntries_Position", "Position >= 0"));
        });
    }
}

public sealed class StoredTrack
{
    public long Id { get; set; }
    public required string PathKey { get; set; }
    public required string FilePath { get; set; }
    public required string Title { get; set; }
    public string? Artist { get; set; }
    public string? MusicBrainzArtistId { get; set; }
    public int MetadataVersion { get; set; }
    public string? Album { get; set; }
    public string? ReleaseTypeTag { get; set; }
    public long DurationTicks { get; set; }
    public long? FileSize { get; set; }
    public long? LastWriteTimeUtcTicks { get; set; }
    public bool IsMissing { get; set; }
    public bool ExplicitlyAddedToLibrary { get; set; }
}

public sealed class StoredArtistIdentity
{
    public required string LookupKey { get; set; }
    public MusicPlayer.Models.ArtistIdentityStatus Status { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? Name { get; set; }
    public long ExpiresAtUtcTicks { get; set; }
}

public sealed class StoredReleaseType
{
    public required string ReleaseKey { get; set; }
    public MusicPlayer.Models.ReleaseType Type { get; set; }
}

public sealed class StoredMusicFolder
{
    public required string PathKey { get; set; }
    public required string Path { get; set; }
    public bool IncludeSubdirectories { get; set; }
    public bool DiscoverNewTracks { get; set; }
}

public sealed class StoredPlaylist
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    public List<StoredPlaylistEntry> Entries { get; set; } = [];
}

public sealed class StoredPlaylistEntry
{
    public Guid PlaylistId { get; set; }
    public int Position { get; set; }
    public long TrackId { get; set; }
    public StoredTrack Track { get; set; } = null!;
}

public sealed class StorageState
{
    public required string Key { get; set; }
}

public sealed class StoredPlaybackSession
{
    public int Id { get; set; } = 1;
    public long? CurrentTrackId { get; set; }
    public StoredTrack? CurrentTrack { get; set; }
    public long PositionTicks { get; set; }
}

public sealed class StoredQueueEntry
{
    public int Position { get; set; }
    public long TrackId { get; set; }
    public StoredTrack Track { get; set; } = null!;
}

public sealed class StoredPlaybackHistoryEntry
{
    public int Position { get; set; }
    public long TrackId { get; set; }
    public StoredTrack Track { get; set; } = null!;
    public bool Recycled { get; set; }
}
