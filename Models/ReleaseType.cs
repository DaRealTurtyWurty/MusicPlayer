namespace MusicPlayer.Models;

public enum ReleaseType { Unknown, Album, Single, EP, Other }

public sealed record ReleaseTypeChoice(ReleaseType? Type, string Label);

public sealed record ReleaseClassification(ReleaseType Type, string Explanation)
{
    public static ReleaseClassification FromTracks(IEnumerable<Track> tracks)
    {
        var releaseTracks = tracks.ToArray();
        var types = releaseTracks.SelectMany(t => (t.ReleaseTypeTag ?? "").Split([';', '/', ',', '\0'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim().ToLowerInvariant() switch
            {
                "album" => ReleaseType.Album,
                "single" => ReleaseType.Single,
                "ep" => ReleaseType.EP,
                "other" or "broadcast" => ReleaseType.Other,
                _ => (ReleaseType?)null // Compilation/live/remix are secondary types, not alternatives to Album/Single/EP.
            }).Where(type => type is not null).Select(type => type!.Value).Distinct().ToArray();
        return types.Length switch
        {
            1 => new(types[0], "From embedded release-type tags."),
            > 1 => new(ReleaseType.Unknown, "The tracks have conflicting release-type tags. Choose a type to resolve this."),
            _ when releaseTracks.Length == 1 && !string.IsNullOrWhiteSpace(releaseTracks[0].Album) &&
                string.Equals(releaseTracks[0].Title.Trim(), releaseTracks[0].Album!.Trim(), StringComparison.OrdinalIgnoreCase)
                => new(ReleaseType.Single, "Inferred single: the only track has the same title as the album. Re-evaluated when the library changes or is rescanned."),
            _ => new(ReleaseType.Unknown, "No release-type tag found and the single-track title rule does not match. Choose a type manually.")
        };
    }
}
