using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IPlaylistStore
{
    IReadOnlyList<Playlist> Load();
    void Save(IEnumerable<Playlist> playlists);
}