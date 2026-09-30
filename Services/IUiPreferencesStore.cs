using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IUiPreferencesStore
{
    CrossfadeOptions LoadCrossfade() => new();
    void SaveCrossfade(CrossfadeOptions options) { }
    ReplayGainOptions LoadReplayGain() => new();
    void SaveReplayGain(ReplayGainOptions options) { }
    LibraryWorkflowPreferences LoadLibraryWorkflow() => new();
    void SaveLibraryWorkflow(LibraryWorkflowPreferences preferences) { }
    string? LoadOutputDeviceId() => null;
    void SaveOutputDeviceId(string? deviceId) { }
    bool LoadGaplessPlaybackEnabled() => true;
    void SaveGaplessPlaybackEnabled(bool enabled) { }
    bool LoadQueueOpen();
    void SaveQueueOpen(bool isOpen);
    AppPage LoadSelectedPage();
    void SaveSelectedPage(AppPage page);
    (double Volume, double VolumeBeforeMute) LoadVolume();
    void SaveVolume(double volume, double volumeBeforeMute);
    (bool ShuffleEnabled, PlaybackRepeatMode RepeatMode) LoadPlaybackModes();
    void SavePlaybackModes(bool shuffleEnabled, PlaybackRepeatMode repeatMode);
    bool LoadLyricsEnabled();
    void SaveLyricsEnabled(bool enabled);
    DiscordPresenceOptions LoadDiscordPresence();
    void SaveDiscordPresence(DiscordPresenceOptions options);
}
