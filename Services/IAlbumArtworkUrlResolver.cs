using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IAlbumArtworkUrlResolver
{
    Task<string?> ResolveAsync(Track track, CancellationToken cancellationToken);
}
