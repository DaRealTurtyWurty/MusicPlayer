using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels.Coordination;

// The view model supplies bindable state; this coordinator owns audio transitions.
internal interface IPlaybackState
{
    Track? CurrentTrack { get; set; }
    double PositionSeconds { get; set; }
    double DurationSeconds { get; set; }
    string? PlaybackError { get; set; }
    bool IsPlaying { get; set; }
    bool IsPlaybackStopped { get; set; }
    bool ReloadCurrentTrack { get; set; }
    PlaybackRepeatMode RepeatMode { get; }
    bool IsGaplessPlaybackEnabled { get; }
    bool IsCrossfadeEnabled { get; }
    ObservableCollection<Track> Queue { get; }
    LyricsViewModel Lyrics { get; }
    void RememberTrack(Track? track, bool recycled = false);
    void Next();
    void RefreshLyricsPosition();
    void SetPositionFromOutput(double seconds);
    void ListeningStarted(Track track);
}

internal sealed class PlaybackCoordinator : IDisposable
{
    private readonly IPlaybackState _state;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IMetadataService _metadataService;
    private bool _listenRecorded;

    public void RecordPlaybackStart()
    {
        if (_listenRecorded || !_state.IsPlaying || !OutputIsPlaying || _state.CurrentTrack is not { } track) return;
        _listenRecorded = true;
        _state.ListeningStarted(track);
    }

    public PlaybackCoordinator(IPlaybackState state, IAudioPlayer audioPlayer, IMetadataService metadataService)
    {
        _state = state;
        _audioPlayer = audioPlayer;
        _metadataService = metadataService;
    }

    public void Initialize()
    {
        _audioPlayer.PlaybackEnded += OnPlaybackEnded;
        if (_audioPlayer is IGaplessAudioPlayer gapless) gapless.NextTrackStarted += OnGaplessTrackStarted;
        _state.Queue.CollectionChanged += OnGaplessQueueChanged;
    }

    private bool OutputIsPlaying => _audioPlayer is not IAudioDevicePlayer player || player.IsOutputPlaying;

    public void Play()
    {
        if (_state.ReloadCurrentTrack && _state.CurrentTrack is { } relocated)
        {
            var position = _state.PositionSeconds;
            if (LoadTrack(relocated, playImmediately: true, rememberCurrent: false))
                _state.PositionSeconds = Math.Clamp(position, 0, _state.DurationSeconds);
            return;
        }
        if (_state.CurrentTrack is null)
        {
            _state.Next();
            return;
        }

        if (!TryPlaybackAction(() =>
            {
                if (_audioPlayer.Position >= _audioPlayer.Duration)
                {
                    _audioPlayer.Seek(TimeSpan.Zero);
                    _listenRecorded = false;
                }
                _audioPlayer.Play();
            })) return;
        _state.IsPlaybackStopped = false;
        _state.IsPlaying = OutputIsPlaying;
        RecordPlaybackStart();
    }

    public bool LoadTrack(Track track, bool playImmediately, bool rememberCurrent = true)
    {
        var previous = _state.CurrentTrack;
        try
        {
            // Saved tracks keep metadata; artwork is read from the music file on demand.
            if (track.ArtworkData is null && System.IO.File.Exists(track.FilePath))
            {
                try
                {
                    track = _metadataService.ReadTrack(track.FilePath);
                }
                catch
                {
                    /* Playback can still succeed when tags cannot be read. */
                }
            }

            // Reloading a relocated current file continues its existing listening occurrence.
            var continuingListen = !rememberCurrent && _state.ReloadCurrentTrack && previous is not null &&
                StringComparer.OrdinalIgnoreCase.Equals(previous.FilePath, track.FilePath);
            _audioPlayer.Load(track.FilePath);
            if (!continuingListen) _listenRecorded = false;
            _state.ReloadCurrentTrack = false;
            _state.CurrentTrack = track;
            _state.PlaybackError = null;

            _state.DurationSeconds = _audioPlayer.Duration.TotalSeconds;
            _state.PositionSeconds = 0;

            PrepareGaplessTrack();
            if (playImmediately)
                _audioPlayer.Play();
            _state.IsPlaybackStopped = false;
            _state.IsPlaying = playImmediately && OutputIsPlaying;
            RecordPlaybackStart();
            if (rememberCurrent)
                _state.RememberTrack(previous);
            return true;
        }
        catch (Exception ex)
        {
            _state.IsPlaying = false;
            _state.CurrentTrack = null;
            _state.DurationSeconds = 0;
            _state.PositionSeconds = 0;
            _state.PlaybackError = $"Could not play {track.Title}: {ex.Message}";
            if (rememberCurrent) _state.RememberTrack(previous);
            return false;
        }
    }

    public void Pause()
    {
        if (!TryPlaybackAction(_audioPlayer.Pause)) return;
        _state.IsPlaybackStopped = false;
        _state.IsPlaying = false;
    }

    public void Stop()
    {
        if (!TryPlaybackAction(_audioPlayer.Stop)) return;
        _state.IsPlaybackStopped = true;
        _state.IsPlaying = false;
        _state.PositionSeconds = 0;
        _listenRecorded = false;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        _state.IsPlaybackStopped = true;
        _state.IsPlaying = false;
        if (_state.RepeatMode == PlaybackRepeatMode.One && _state.CurrentTrack is not null)
        {
            if (LoadTrack(_state.CurrentTrack, playImmediately: true, rememberCurrent: false))
                return;
        }

        _state.Next();
    }

    public bool TryPlaybackAction(Action action)
    {
        try
        {
            action();
            _state.PlaybackError = null;
            return true;
        }
        catch (Exception ex)
        {
            _state.IsPlaying = false;
            _state.PlaybackError = $"Could not control playback: {ex.Message}";
            return false;
        }
    }

    private Track? _preparedTrack;
    private Track? _preparedQueueTrack;
    private bool _committingGapless;

    private void OnGaplessQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => PrepareGaplessTrack();

    public void PrepareGaplessTrack()
    {
        if (_committingGapless || _audioPlayer is not IGaplessAudioPlayer player) return;
        var next = (!_state.IsGaplessPlaybackEnabled && !_state.IsCrossfadeEnabled) || _state.CurrentTrack is null ? null : _state.RepeatMode == PlaybackRepeatMode.One
            ? _state.CurrentTrack : _state.Queue.FirstOrDefault() ?? (_state.RepeatMode == PlaybackRepeatMode.All ? _state.CurrentTrack : null);
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
        var previous = _state.CurrentTrack;
        _committingGapless = true;
        try
        {
            if (_state.RepeatMode != PlaybackRepeatMode.One)
            {
                var recycled = _state.RepeatMode == PlaybackRepeatMode.All && previous is not null;
                var hadUpcoming = _state.Queue.Count > 0;
                if (hadUpcoming) _state.Queue.RemoveAt(0);
                if (recycled && hadUpcoming) _state.Queue.Add(previous!);
                _state.RememberTrack(previous, recycled);
            }
            _state.CurrentTrack = next;
            _listenRecorded = false;
            _state.Lyrics.SetTrack(next, _audioPlayer.Duration);
            _state.ReloadCurrentTrack = false;
            _state.PlaybackError = null;
            _state.DurationSeconds = _audioPlayer.Duration.TotalSeconds;
            _state.SetPositionFromOutput(_audioPlayer.PresentationPosition.TotalSeconds);
            _state.IsPlaybackStopped = false;
            _state.IsPlaying = OutputIsPlaying;
            RecordPlaybackStart();
            _state.RefreshLyricsPosition();
        }
        finally { _committingGapless = false; }
        _preparedTrack = null;
        _preparedQueueTrack = null;
        PrepareGaplessTrack();
    }

    public void Dispose()
    {
        _audioPlayer.PlaybackEnded -= OnPlaybackEnded;
        if (_audioPlayer is IGaplessAudioPlayer gapless) gapless.NextTrackStarted -= OnGaplessTrackStarted;
        _state.Queue.CollectionChanged -= OnGaplessQueueChanged;
    }
}
