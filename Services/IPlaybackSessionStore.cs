using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IPlaybackSessionStore
{
    PlaybackSession LoadSession();
    void SaveSession(PlaybackSession session);
    void SavePosition(TimeSpan position);
}
