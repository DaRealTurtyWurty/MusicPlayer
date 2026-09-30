using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private bool _initializingReplayGain;
    public IReadOnlyList<ReplayGainMode> ReplayGainModes { get; } = Enum.GetValues<ReplayGainMode>();
    [ObservableProperty] private bool isReplayGainEnabled = true;
    [ObservableProperty] private ReplayGainMode selectedReplayGainMode = ReplayGainMode.Track;
    [ObservableProperty] private double replayGainPreampDb;
    [ObservableProperty] private bool replayGainPreventClipping = true;

    private void InitializeReplayGain()
    {
        var options = (_uiPreferencesStore?.LoadReplayGain() ?? new()).Normalize();
        _initializingReplayGain = true;
        IsReplayGainEnabled = options.Enabled;
        SelectedReplayGainMode = options.Mode;
        ReplayGainPreampDb = options.PreampDb;
        ReplayGainPreventClipping = options.PreventClipping;
        _initializingReplayGain = false;
        if (_audioPlayer is IReplayGainAudioPlayer player) player.ReplayGainOptions = options;
    }

    private void UpdateReplayGain()
    {
        if (_initializingReplayGain) return;
        var options = new ReplayGainOptions(IsReplayGainEnabled, SelectedReplayGainMode,
            ReplayGainPreampDb, ReplayGainPreventClipping).Normalize();
        if (_audioPlayer is IReplayGainAudioPlayer player) player.ReplayGainOptions = options;
        _uiPreferencesStore?.SaveReplayGain(options);
    }

    partial void OnIsReplayGainEnabledChanged(bool value) => UpdateReplayGain();
    partial void OnSelectedReplayGainModeChanged(ReplayGainMode value) => UpdateReplayGain();
    partial void OnReplayGainPreampDbChanged(double value) => UpdateReplayGain();
    partial void OnReplayGainPreventClippingChanged(bool value) => UpdateReplayGain();
}
