using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<PlaybackShuffleMode> ShuffleModes { get; } = Enum.GetValues<PlaybackShuffleMode>();
    [ObservableProperty] private bool avoidConsecutiveArtists;
    [ObservableProperty] private bool avoidConsecutiveAlbums;

    partial void OnAvoidConsecutiveArtistsChanged(bool value) => SaveShuffleRules();
    partial void OnAvoidConsecutiveAlbumsChanged(bool value) => SaveShuffleRules();

    private void SaveShuffleRules()
    {
        _uiPreferencesStore?.SaveShuffleRules(new(AvoidConsecutiveArtists, AvoidConsecutiveAlbums));
        if (IsShuffleEnabled) ShuffleQueue();
    }
}
