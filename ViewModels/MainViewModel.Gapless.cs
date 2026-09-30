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

    private Track? _preparedTrack;
    private Track? _preparedQueueTrack;
    private bool _committingGapless;

    private void OnGaplessQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => PrepareGaplessTrack();

    private void PrepareGaplessTrack()
    {
        if (_committingGapless || _audioPlayer is not IGaplessAudioPlayer player) return;
        var next = !IsGaplessPlaybackEnabled || CurrentTrack is null ? null : RepeatMode == PlaybackRepeatMode.One
            ? CurrentTrack : Queue.FirstOrDefault() ?? (RepeatMode == PlaybackRepeatMode.All ? CurrentTrack : null);
        try
        {
            player.PrepareNext(next?.FilePath);
            if (!ReferenceEquals(next, _preparedQueueTrack))
            {
                _preparedTrack = next;
                if (next is { ArtworkData: null } && System.IO.File.Exists(next.FilePath))
                {
                    try { _preparedTrack = _metadataService.ReadTrack(next.FilePath); }
                    catch { /* Audio playback can succeed without readable tags. */ }
                }
            }
            _preparedQueueTrack = next;
        }
        catch
        {
            // Normal EOF advancement reports/skips unreadable files. Preloading
            // must neither consume queue entries nor interrupt the current song.
            _preparedTrack = null;
            _preparedQueueTrack = null;
        }
    }

    private void OnGaplessTrackStarted(object? sender, EventArgs e)
    {
        if (_preparedTrack is not { } next) return;
        var previous = CurrentTrack;
        _committingGapless = true;
        try
        {
            if (RepeatMode != PlaybackRepeatMode.One)
            {
                var recycled = RepeatMode == PlaybackRepeatMode.All && previous is not null;
                var hadUpcoming = Queue.Count > 0;
                if (hadUpcoming) Queue.RemoveAt(0);
                if (recycled && hadUpcoming) Queue.Add(previous!);
                RememberTrack(previous, recycled);
            }
            CurrentTrack = next;
            Lyrics.SetTrack(next, _audioPlayer.Duration);
            _reloadCurrentTrack = false;
            PlaybackError = null;
            DurationSeconds = _audioPlayer.Duration.TotalSeconds;
            _updatingPosition = true;
            try { PositionSeconds = _audioPlayer.PresentationPosition.TotalSeconds; }
            finally { _updatingPosition = false; }
            IsPlaybackStopped = false;
            IsPlaying = OutputIsPlaying;
            RefreshLyricsPosition();
        }
        finally { _committingGapless = false; }
        _preparedTrack = null;
        _preparedQueueTrack = null;
        PrepareGaplessTrack();
    }
}
