using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private IPlaybackSessionStore? _playbackSessionStore;
    private DispatcherTimer? _sessionSaveTimer;
    private bool _canSaveSession;
    private bool _sessionDirty;
    private TimeSpan _savedSessionPosition;

    private void InitializePlaybackSession(IPlaybackSessionStore? store)
    {
        _playbackSessionStore = store;
        if (store is null) return;
        try
        {
            var session = store.LoadSession();
            foreach (var entry in session.History) _playbackHistory.Push((entry.Track, entry.Recycled));
            RefreshTimelineHistory();
            PreviousCommand.NotifyCanExecuteChanged();
            foreach (var track in session.Queue) Queue.Add(track);
            if (session.CurrentTrack is { } current && LoadTrack(current, playImmediately: false, rememberCurrent: false))
                PositionSeconds = Math.Clamp(session.Position.TotalSeconds, 0, DurationSeconds);
            _savedSessionPosition = CurrentTrack is null ? TimeSpan.Zero : _audioPlayer.Position;
            _canSaveSession = true;
        }
        catch (Exception ex)
        {
            // Preserve an unreadable session instead of overwriting it with an empty queue.
            PlaybackError = $"Could not restore playback session: {ex.Message}";
        }
        Queue.CollectionChanged += OnSessionQueueChanged;
        _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sessionSaveTimer.Tick += OnSessionSaveTick;
        _sessionSaveTimer.Start();
    }

    partial void OnCurrentTrackChanged(Track? value)
    {
        Lyrics.SetTrack(value, _audioPlayer.Duration);
        RefreshTimelineHistory();
        if (_canSaveSession) _sessionDirty = true;
    }

    private void OnSessionQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => _sessionDirty = true;
    private void OnSessionSaveTick(object? sender, EventArgs e) => SavePlaybackSession();

    private void SavePlaybackSession()
    {
        if (!_canSaveSession || _playbackSessionStore is null || _isRelinkingTrack) return;
        try
        {
            var position = CurrentTrack is null ? TimeSpan.Zero : _audioPlayer.Position;
            if (_sessionDirty)
                _playbackSessionStore.SaveSession(new PlaybackSession(CurrentTrack, position, Queue.ToArray())
                {
                    History = _playbackHistory.Reverse()
                        .Select(entry => new PlaybackHistoryEntry(entry.Track, entry.Recycled)).ToArray()
                });
            else if (position != _savedSessionPosition)
                _playbackSessionStore.SavePosition(position);
            _sessionDirty = false;
            _savedSessionPosition = position;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not save playback session: {ex.Message}");
            PlaybackError = $"Could not save playback session: {ex.Message}";
        }
    }

    private void DisposePlaybackSession()
    {
        if (_sessionSaveTimer is not null)
        {
            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Tick -= OnSessionSaveTick;
        }
        Queue.CollectionChanged -= OnSessionQueueChanged;
        // Read the final backend position before disposing the audio device.
        SavePlaybackSession();
        _canSaveSession = false;
    }
}
