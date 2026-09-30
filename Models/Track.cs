namespace MusicPlayer.Models;

public sealed class Track : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private long _playCount;
    public long PlayCount
    {
        get => _playCount;
        set { if (SetProperty(ref _playCount, value)) OnPropertyChanged(nameof(PlayCountText)); }
    }
    public string PlayCountText => $"{PlayCount} {(PlayCount == 1 ? "play" : "plays")}";
    public required string FilePath { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }
    public string? MusicBrainzArtistId { get; init; }
    public const int CurrentMetadataVersion = 3;
    // Older entries are reread once to recover newly supported tags.
    public int MetadataVersion { get; init; } = CurrentMetadataVersion;

    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }
    public string? ReleaseTypeTag { get; init; }
    public uint DiscNumber { get; init; }
    public uint TrackNumber { get; init; }
    public uint Year { get; init; }
    public string? Genre { get; init; }

    public TimeSpan Duration { get; init; }

    public byte[]? ArtworkData { get; init; }

    public long? FileSize { get; init; }
    public long? LastWriteTimeUtcTicks { get; init; }
    public bool IsMissing { get; init; }
    public bool ExplicitlyAddedToLibrary { get; set; }

    public Track WithFileState(long? size, long? modified, bool missing) => new()
    {
        FilePath = FilePath, Title = Title, Artist = Artist, Album = Album, Duration = Duration,
        ReleaseTypeTag = ReleaseTypeTag,
        DiscNumber = DiscNumber, TrackNumber = TrackNumber, Year = Year, Genre = Genre,
        AlbumArtist = AlbumArtist, MusicBrainzReleaseId = MusicBrainzReleaseId,
        MusicBrainzReleaseGroupId = MusicBrainzReleaseGroupId,
        MusicBrainzArtistId = MusicBrainzArtistId, MetadataVersion = MetadataVersion,
        ArtworkData = ArtworkData, FileSize = size, LastWriteTimeUtcTicks = modified, IsMissing = missing,
        ExplicitlyAddedToLibrary = ExplicitlyAddedToLibrary, PlayCount = PlayCount
    };

    public string DurationText =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
}
