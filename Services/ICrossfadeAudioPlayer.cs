using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ICrossfadeAudioPlayer
{
    CrossfadeOptions CrossfadeOptions { get; set; }
}
