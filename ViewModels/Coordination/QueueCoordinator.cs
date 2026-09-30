using System.Collections.ObjectModel;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels.Coordination;

internal interface IQueuePlaybackState
{
    Track? CurrentTrack { get; }
    PlaybackRepeatMode RepeatMode { get; }
    bool IsShuffleEnabled { get; }
    int SelectedQueueIndex { get; set; }
    string? PlaybackError { get; set; }
    double PositionSeconds { get; set; }
    TimeSpan PlaybackPosition { get; }
    bool LoadTrack(Track track, bool playImmediately, bool rememberCurrent = true);
    bool SeekToStart();
    void Play();
    void HistoryChanged();
    void PreviousAvailabilityChanged();
}

// Queue entries are occurrences: duplicate tracks retain independent selection and history.
internal sealed class QueueCoordinator(IQueuePlaybackState state, Random random)
{
    private readonly IQueuePlaybackState _state = state;
    private readonly Random _random = random;
    private readonly Stack<(Track Track, bool Recycled)> _history = new();
    public ObservableCollection<Track> Queue { get; } = [];
    public IEnumerable<(Track Track, bool Recycled)> History => _history;
    public bool CanAdvance => Queue.Count > 0 || (_state.RepeatMode == PlaybackRepeatMode.All && _state.CurrentTrack is not null);
    public bool CanGoPrevious => _state.CurrentTrack is not null || _history.Count > 0;
    public bool HasSelection => _state.SelectedQueueIndex >= 0 && _state.SelectedQueueIndex < Queue.Count;
    public bool CanMoveUp => HasSelection && _state.SelectedQueueIndex > 0;
    public bool CanMoveDown => HasSelection && _state.SelectedQueueIndex < Queue.Count - 1;

    public void RestoreHistory(IEnumerable<(Track Track, bool Recycled)> entries)
    {
        _history.Clear();
        foreach (var entry in entries) _history.Push(entry);
        _state.HistoryChanged();
    }

    public void ReplaceHistoryTracks(Func<Track, Track> replace)
    {
        var entries = _history.Reverse().Select(e => (Track: replace(e.Track), e.Recycled)).ToArray();
        _history.Clear();
        foreach (var entry in entries) _history.Push(entry);
    }

    public void ShuffleQueue()
    {
        // Track indices, rather than track identity, preserve selection for duplicate songs.
        var selectedIndex = _state.SelectedQueueIndex;
        for (var i = Queue.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            if (i == j) continue;
            (Queue[i], Queue[j]) = (Queue[j], Queue[i]);
            if (selectedIndex == i) selectedIndex = j;
            else if (selectedIndex == j) selectedIndex = i;
        }

        _state.SelectedQueueIndex = selectedIndex;
    }

    public void StartPlaybackSession(IEnumerable<Track> tracks)
    {
        var sessionTracks = tracks.ToArray();
        _history.Clear();
        _state.HistoryChanged();
        Queue.Clear();
        foreach (var track in sessionTracks)
            Queue.Add(track);
        if (_state.IsShuffleEnabled)
            ShuffleQueue();
        // A new library session must not recycle the previously playing track.
        AdvanceQueue(recycleCurrent: false);
    }

    public void Previous()
    {
        if (_state.CurrentTrack is not null && (_state.PlaybackPosition.TotalSeconds > 3 || _history.Count == 0))
        {
            if (!_state.SeekToStart()) return;
            _state.PositionSeconds = 0;
            _state.Play();
            return;
        }

        var interruptedTrack = _state.CurrentTrack;
        while (_history.TryPop(out var previous))
        {
            _state.HistoryChanged();
            // Repeat-all places the played entry at the tail. Pull it back out when
            // navigating backwards, so going forward does not grow the repeat cycle.
            if (previous.Recycled && Queue.Count > 0 &&
                string.Equals(Queue[^1].FilePath, previous.Track.FilePath, StringComparison.OrdinalIgnoreCase))
                Queue.RemoveAt(Queue.Count - 1);
            if (!_state.LoadTrack(previous.Track, playImmediately: true, rememberCurrent: false))
                continue;
            if (interruptedTrack is not null)
                Queue.Insert(0, interruptedTrack);
            _state.PreviousAvailabilityChanged();
            return;
        }

        if (interruptedTrack is not null)
            _state.LoadTrack(interruptedTrack, playImmediately: true, rememberCurrent: false);
        _state.PreviousAvailabilityChanged();
    }

    public void RememberTrack(Track? track, bool recycled = false)
    {
        if (track is null) return;
        _history.Push((track, recycled));
        _state.HistoryChanged();
    }

    public void AdvanceQueue(bool recycleCurrent)
    {
        var previous = _state.CurrentTrack;
        var recycled = recycleCurrent && _state.RepeatMode == PlaybackRepeatMode.All && previous is not null;
        if (recycled)
            Queue.Add(previous!);

        // Unreadable tracks should not prevent the rest of the queue from playing.
        // Recycle only once per advance so a queue of failed files cannot loop forever.
        var skipped = new List<string>();
        while (Queue.Count > 0)
        {
            var track = Queue[0];
            Queue.RemoveAt(0);
            if (_state.LoadTrack(track, playImmediately: true, rememberCurrent: false))
            {
                if (recycleCurrent)
                    RememberTrack(previous, recycled);
                if (skipped.Count > 0) _state.PlaybackError = string.Join(Environment.NewLine, skipped);
                return;
            }
            if (_state.PlaybackError is { } error) skipped.Add(error);
        }
        if (skipped.Count > 0) _state.PlaybackError = string.Join(Environment.NewLine, skipped);
        if (recycleCurrent && _state.CurrentTrack is null)
            RememberTrack(previous, recycled);
    }

    public void PlayQueuedTrack()
    {
        if (!HasSelection) return;
        var track = Queue[_state.SelectedQueueIndex];
        Queue.RemoveAt(_state.SelectedQueueIndex);
        var previous = _state.CurrentTrack;
        var recycled = _state.RepeatMode == PlaybackRepeatMode.All && previous is not null;
        if (recycled)
            Queue.Add(previous!);
        var loaded = _state.LoadTrack(track, playImmediately: true, rememberCurrent: false);
        RememberTrack(previous, recycled);
        if (!loaded)
            AdvanceQueue(recycleCurrent: false);
    }

    public void RemoveFromQueue()
    {
        if (!HasSelection) return;
        var index = _state.SelectedQueueIndex;
        Queue.RemoveAt(index);
        _state.SelectedQueueIndex = Math.Min(index, Queue.Count - 1);
    }

    public void MoveQueueUp()
    {
        if (!CanMoveUp) return;
        var index = _state.SelectedQueueIndex;
        Queue.Move(index, index - 1);
        _state.SelectedQueueIndex = index - 1;
    }

    public void MoveQueueDown()
    {
        if (!CanMoveDown) return;
        var index = _state.SelectedQueueIndex;
        Queue.Move(index, index + 1);
        _state.SelectedQueueIndex = index + 1;
    }
}
