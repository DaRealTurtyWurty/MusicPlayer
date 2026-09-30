using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private bool _initializingCrossfade;
    [ObservableProperty] private bool isCrossfadeEnabled;
    [ObservableProperty] private double crossfadeDurationSeconds = 5;

    private void InitializeCrossfade()
    {
        var options = (_uiPreferencesStore?.LoadCrossfade() ?? new()).Normalize();
        _initializingCrossfade = true;
        IsCrossfadeEnabled = options.Enabled;
        CrossfadeDurationSeconds = options.DurationSeconds;
        _initializingCrossfade = false;
        if (_audioPlayer is ICrossfadeAudioPlayer player) player.CrossfadeOptions = options;
    }

    private void UpdateCrossfade()
    {
        if (_initializingCrossfade) return;
        var options = new CrossfadeOptions(IsCrossfadeEnabled, CrossfadeDurationSeconds).Normalize();
        if (_audioPlayer is ICrossfadeAudioPlayer player) player.CrossfadeOptions = options;
        PrepareGaplessTrack();
        _uiPreferencesStore?.SaveCrossfade(options);
    }

    partial void OnIsCrossfadeEnabledChanged(bool value) => UpdateCrossfade();
    partial void OnCrossfadeDurationSecondsChanged(double value) => UpdateCrossfade();
}
