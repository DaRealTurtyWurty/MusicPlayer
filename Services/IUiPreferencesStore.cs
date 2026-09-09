using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IUiPreferencesStore
{
    bool LoadQueueOpen();
    void SaveQueueOpen(bool isOpen);
    AppPage LoadSelectedPage();
    void SaveSelectedPage(AppPage page);
    (double Volume, double VolumeBeforeMute) LoadVolume();
    void SaveVolume(double volume, double volumeBeforeMute);
    (bool ShuffleEnabled, PlaybackRepeatMode RepeatMode) LoadPlaybackModes();
    void SavePlaybackModes(bool shuffleEnabled, PlaybackRepeatMode repeatMode);
}
