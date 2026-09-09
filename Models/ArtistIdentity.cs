namespace MusicPlayer.Models;

public enum ArtistIdentityStatus { Identified, NotFound, Ambiguous, Unavailable }
public enum ArtistIdentitySource { Tags, MusicBrainz }

public sealed record ArtistIdentity(ArtistIdentityStatus Status, string? MusicBrainzId = null,
    string? Name = null, ArtistIdentitySource Source = ArtistIdentitySource.MusicBrainz);

public static class MusicBrainzId
{
    public static string? Normalize(string? value) =>
        Guid.TryParseExact(value?.Trim(), "D", out var id) && id != Guid.Empty ? id.ToString("D") : null;
}
