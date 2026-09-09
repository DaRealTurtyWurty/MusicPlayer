using MusicPlayer.Models;
using Windows.Media;

namespace MusicPlayer.Services;

public sealed record SystemMediaState(
    Track? Track,
    MediaPlaybackStatus Status,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    TimeSpan Position,
    TimeSpan Duration);

public interface ISystemMediaControls : IDisposable
{
    event Action<SystemMediaTransportControlsButton>? ButtonPressed;
    event Action<TimeSpan>? PositionRequested;
    void Update(SystemMediaState state);
}
