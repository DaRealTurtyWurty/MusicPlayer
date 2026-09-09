namespace MusicPlayer.Models;

public sealed class Track
{
    public required string FilePath { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }
    public string? ReleaseTypeTag { get; init; }

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
        ArtworkData = ArtworkData, FileSize = size, LastWriteTimeUtcTicks = modified, IsMissing = missing,
        ExplicitlyAddedToLibrary = ExplicitlyAddedToLibrary
    };

    public string DurationText =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
}
