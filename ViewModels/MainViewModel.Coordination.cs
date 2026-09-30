using MusicPlayer.Models;
using MusicPlayer.ViewModels.Coordination;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    bool IPlaybackState.ReloadCurrentTrack { get => _reloadCurrentTrack; set => _reloadCurrentTrack = value; }
    void IPlaybackState.RememberTrack(Track? track, bool recycled) => RememberTrack(track, recycled);
    void IPlaybackState.Next() => Next();
    void IPlaybackState.SetPositionFromOutput(double seconds) => SetPositionFromOutput(seconds);

    bool IQueuePlaybackState.LoadTrack(Track track, bool playImmediately, bool rememberCurrent) => LoadTrack(track, playImmediately, rememberCurrent);
    bool IQueuePlaybackState.SeekToStart() => TryPlaybackAction(() => _audioPlayer.Seek(TimeSpan.Zero));
    void IQueuePlaybackState.Play() => Play();
    void IQueuePlaybackState.HistoryChanged()
    {
        RefreshTimelineHistory();
        PreviousCommand.NotifyCanExecuteChanged();
    }
    void IQueuePlaybackState.PreviousAvailabilityChanged() => PreviousCommand.NotifyCanExecuteChanged();

    bool ILibraryState.ApplyingTrackUpdates => _applyingTrackUpdates;
    bool ILibraryState.IsRelinkingTrack => _isRelinkingTrack;
    IEnumerable<Track> ILibraryState.KnownPlaybackTracks() => KnownPlaybackTracks();
    void ILibraryState.ScheduleLibraryRefresh() => ScheduleLibraryRefresh();
    void ILibraryState.MembershipChanged(bool refreshView)
    {
        if (refreshView) LibraryTracks.Refresh();
        OnPropertyChanged(nameof(DeletePlaylistLibraryImpact));
        OnPropertyChanged(nameof(PlaylistExclusiveSongCount));
        OnPropertyChanged(nameof(HasPlaylistExclusiveSongs));
    }

    private void SetPositionFromOutput(double seconds)
    {
        _updatingPosition = true;
        try { PositionSeconds = seconds; }
        finally { _updatingPosition = false; }
    }
}
