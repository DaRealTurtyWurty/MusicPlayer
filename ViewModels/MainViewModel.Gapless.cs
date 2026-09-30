using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool isGaplessPlaybackEnabled = true;

    partial void OnIsGaplessPlaybackEnabledChanged(bool value)
    {
        PrepareGaplessTrack();
        _uiPreferencesStore?.SaveGaplessPlaybackEnabled(value);
    }

    private void PrepareGaplessTrack() => _playback.PrepareGaplessTrack();
}
