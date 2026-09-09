using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IArtistIdentityService
{
    // Local tags/cache only. Null means a network lookup may be needed.
    // Called on a worker thread so persistent-cache reads never block the UI.
    ArtistIdentity? GetKnownIdentity(string artist, IReadOnlyList<Track> tracks) => null;
    Task<ArtistIdentity> IdentifyAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken cancellationToken);
}

public sealed record CachedArtistIdentity(ArtistIdentity Identity, DateTimeOffset ExpiresAt);

public interface IArtistIdentityStore
{
    CachedArtistIdentity? LoadArtistIdentity(string key);
    void SaveArtistIdentity(string key, CachedArtistIdentity entry);
}
