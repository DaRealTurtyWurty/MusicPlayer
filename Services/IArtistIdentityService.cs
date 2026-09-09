using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IArtistIdentityService
{
    Task<ArtistIdentity> IdentifyAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken cancellationToken);
}

public sealed record CachedArtistIdentity(ArtistIdentity Identity, DateTimeOffset ExpiresAt);

public interface IArtistIdentityStore
{
    CachedArtistIdentity? LoadArtistIdentity(string key);
    void SaveArtistIdentity(string key, CachedArtistIdentity entry);
}
