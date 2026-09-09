using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ITrackMatchPicker
{
    Track? PickMatch(Track missing, IReadOnlyList<Track> matches);
}
