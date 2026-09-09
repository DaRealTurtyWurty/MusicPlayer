namespace MusicPlayer.Models;

public enum MusicGroupKind { Album, Artist }

/// <summary>A library-derived album or artist; it never owns or modifies the source tracks.</summary>
public sealed record MusicGroup(
    MusicGroupKind Kind,
    string Name,
    string Artist,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<MusicGroup> Albums)
{
    public ArtistIdentification Identification { get; } = new();
    public ReleaseType ReleaseType { get; init; } = ReleaseType.Unknown;
    public string ReleaseTypeHint { get; init; } = "No release-type tag found.";
    public bool IsRelease => Kind == MusicGroupKind.Album;
    public string ReleaseTypeLabel => ReleaseType == ReleaseType.Unknown ? "Unknown type" : ReleaseType.ToString();
    public string Key => Kind == MusicGroupKind.Artist ? Normalize(Name) : $"{Normalize(Artist)}\0{Normalize(Name)}";
    public string Subtitle => Kind == MusicGroupKind.Album ? Artist : $"{Albums.Count} {(Albums.Count == 1 ? "release" : "releases")}";
    public string Summary => $"{Tracks.Count} {(Tracks.Count == 1 ? "track" : "tracks")}";
    public string DurationSummary
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Tracks.Sum(t => t.Duration.TotalSeconds));
            var time = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} hr {duration.Minutes} min"
                : $"{(int)duration.TotalMinutes} min";
            return $"{Summary} · {time}";
        }
    }

    public static string ArtistName(Track track) => string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist.Trim();
    public static string AlbumName(Track track) => string.IsNullOrWhiteSpace(track.Album) ? "Unknown album" : track.Album.Trim();
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
