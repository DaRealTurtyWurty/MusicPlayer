using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ILibraryMembershipStore
{
    // Includes tracks retained only for playback/history or to prevent automatic rediscovery.
    IReadOnlyList<Track> LoadKnownTracks();
    // Atomically deletes the playlist and optionally retains its exclusive songs in the library.
    void DeletePlaylist(Guid playlistId, bool keepExclusiveSongs);
}
