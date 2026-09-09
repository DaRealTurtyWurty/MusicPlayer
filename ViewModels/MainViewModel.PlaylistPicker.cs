using MusicPlayer.Models;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    public bool CanChoosePlaylist => _canEditPlaylists;

    public bool TryAddTrackToPlaylist(Track track, Playlist playlist)
    {
        if (!_canEditPlaylists || !Playlists.Contains(playlist)) return false;

        var index = playlist.Tracks.Count;
        playlist.Tracks.Add(track);
        SavePlaylists();
        if (PlaylistError is not null)
        {
            // Keep retrying a failed save from adding the same entry twice.
            playlist.Tracks.RemoveAt(index);
            return false;
        }

        PlaylistMessage = $"Added {track.Title} to {playlist.Name}.";
        return true;
    }
}
