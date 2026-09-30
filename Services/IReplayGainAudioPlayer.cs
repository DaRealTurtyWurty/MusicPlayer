using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IReplayGainAudioPlayer
{
    ReplayGainOptions ReplayGainOptions { get; set; }
}
