using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels.Coordination;

internal interface IQueuePlaybackState
{
    Track? CurrentTrack { get; }
    PlaybackRepeatMode RepeatMode { get; }
    bool IsShuffleEnabled { get; }
    PlaybackShuffleMode ShuffleMode { get; }
    bool AvoidConsecutiveArtists { get; }
    bool AvoidConsecutiveAlbums { get; }
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
internal sealed class QueueCoordinator
{
    private readonly IQueuePlaybackState _state;
    private readonly Random _random;
    private readonly List<bool> _pendingCycle = [];
    private bool _addingRecycled;
    private bool _currentPendingNextCycle;

    public QueueCoordinator(IQueuePlaybackState state, Random random)
    {
        _state = state;
        _random = random;
        Queue.CollectionChanged += TrackCycleChanges;
    }

    private void TrackCycleChanges(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                _pendingCycle.Insert(e.NewStartingIndex, !_addingRecycled || _currentPendingNextCycle);
                break;
            case NotifyCollectionChangedAction.Remove:
                _pendingCycle.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Move:
                var pending = _pendingCycle[e.OldStartingIndex];
                _pendingCycle.RemoveAt(e.OldStartingIndex);
                _pendingCycle.Insert(e.NewStartingIndex, pending);
                break;
            case NotifyCollectionChangedAction.Reset:
                _pendingCycle.Clear();
                _currentPendingNextCycle = false;
                break;
        }
    }

    public void AddRecycledTrack(Track track)
    {
        _addingRecycled = true;
        try { Queue.Add(track); }
        finally { _addingRecycled = false; _currentPendingNextCycle = false; }
    }
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

    public void ShuffleQueue(bool newSession = false)
    {
        if (_state.ShuffleMode == PlaybackShuffleMode.Off) return;
        // Original indices identify occurrences even when the same song is queued twice.
        var entries = Queue.Select((track, index) => (Track: track, Index: index)).ToArray();
        var groups = _state.ShuffleMode switch
        {
            PlaybackShuffleMode.Albums => entries.GroupBy(e => MusicGroup.AlbumKey(e.Track))
                .Select(g => g.OrderBy(e => e.Track.DiscNumber == 0 ? 1u : e.Track.DiscNumber)
                    .ThenBy(e => e.Track.TrackNumber == 0 ? ulong.MaxValue : e.Track.TrackNumber)
                    .ToArray()).ToArray(),
            PlaybackShuffleMode.Artists => entries.GroupBy(e => MusicGroup.Normalize(MusicGroup.ArtistName(e.Track)))
                .Select(g => g.ToArray()).ToArray(),
            _ => entries.Select(e => new[] { e }).ToArray()
        };
        for (var i = groups.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }
        if (_state.AvoidConsecutiveArtists || _state.AvoidConsecutiveAlbums)
        {
            var remaining = groups.ToList();
            var arranged = new List<(Track Track, int Index)[]>();
            string ArtistKey(Track track) => MusicGroup.Normalize(MusicGroup.ArtistName(track));
            var artistCounts = remaining.GroupBy(g => ArtistKey(g[0].Track)).ToDictionary(g => g.Key, g => g.Count());
            var albumCounts = remaining.GroupBy(g => MusicGroup.AlbumKey(g[0].Track)).ToDictionary(g => g.Key, g => g.Count());
            var previous = newSession ? null : _state.CurrentTrack;
            var secondPrevious = !newSession && _history.TryPeek(out var lastPlayed) ? lastPlayed.Track : null;
            while (remaining.Count > 0)
            {
                int Conflicts(Track next) => previous is null ? 0 :
                    (_state.AvoidConsecutiveArtists && MusicGroup.Normalize(MusicGroup.ArtistName(previous)) == MusicGroup.Normalize(MusicGroup.ArtistName(next)) ? 1 : 0) +
                    (_state.AvoidConsecutiveAlbums && MusicGroup.AlbumKey(previous) == MusicGroup.AlbumKey(next) ? 1 : 0);
                var largestArtists = artistCounts.OrderByDescending(e => e.Value).Take(2).ToArray();
                var largestAlbums = albumCounts.OrderByDescending(e => e.Value).Take(2).ToArray();
                // Keep enough separators for the remaining songs, without always choosing
                // the two largest categories. All equally suitable candidates retain random order.
                int FutureRepeats(string key, Dictionary<string, int> counts, KeyValuePair<string, int>[] largest)
                {
                    var total = remaining.Count - 1;
                    var count = counts[key] - 1;
                    var otherMax = largest[0].Key == key ? (largest.Length > 1 ? largest[1].Value : 0) : largest[0].Value;
                    return Math.Max(0, count - (total - count)) + Math.Max(0, otherMax - (total - otherMax + 1));
                }
                int StrandedRepeats(Track next) =>
                    (_state.AvoidConsecutiveArtists ? FutureRepeats(ArtistKey(next), artistCounts, largestArtists) : 0) +
                    (_state.AvoidConsecutiveAlbums ? FutureRepeats(MusicGroup.AlbumKey(next), albumCounts, largestAlbums) : 0);
                int ReturnsTooSoon(Track next) => secondPrevious is null ? 0 :
                    (_state.AvoidConsecutiveArtists && ArtistKey(secondPrevious) == ArtistKey(next) ? 1 : 0) +
                    (_state.AvoidConsecutiveAlbums && MusicGroup.AlbumKey(secondPrevious) == MusicGroup.AlbumKey(next) ? 1 : 0);
                var chosen = remaining.OrderBy(g => Conflicts(g[0].Track))
                    .ThenBy(g => StrandedRepeats(g[0].Track))
                    .ThenBy(g => ReturnsTooSoon(g[0].Track)).First();
                arranged.Add(chosen);
                remaining.Remove(chosen);
                artistCounts[ArtistKey(chosen[0].Track)]--;
                albumCounts[MusicGroup.AlbumKey(chosen[0].Track)]--;
                secondPrevious = chosen.Length > 1 ? chosen[^2].Track : previous;
                previous = chosen[^1].Track;
            }
            groups = arranged.ToArray();
        }
        var selectedIndex = _state.SelectedQueueIndex;
        // Move entries so timeline rows and their occurrence identity survive the shuffle.
        var indices = entries.Select(e => e.Index).ToList();
        var order = groups.SelectMany(g => g).ToArray();
        for (var i = 0; i < order.Length; i++)
        {
            var source = indices.IndexOf(order[i].Index, i);
            if (source == i) continue;
            Queue.Move(source, i);
            indices.RemoveAt(source);
            indices.Insert(i, order[i].Index);
        }
        for (var i = 0; i < _pendingCycle.Count; i++) _pendingCycle[i] = true;
        _currentPendingNextCycle = false;
        _state.SelectedQueueIndex = selectedIndex >= 0 && selectedIndex < entries.Length
            ? Array.FindIndex(order, e => e.Index == selectedIndex) : -1;
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
            ShuffleQueue(newSession: true);
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
            if (previous.Recycled && Queue.Count > 0)
            {
                var index = _state.ShuffleMode == PlaybackShuffleMode.ReshuffleEachLoop
                    ? Enumerable.Range(0, Queue.Count).Reverse().FirstOrDefault(i =>
                        string.Equals(Queue[i].FilePath, previous.Track.FilePath, StringComparison.OrdinalIgnoreCase), -1)
                    : string.Equals(Queue[^1].FilePath, previous.Track.FilePath, StringComparison.OrdinalIgnoreCase) ? Queue.Count - 1 : -1;
                if (index >= 0) Queue.RemoveAt(index);
            }
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
        if (recycled && _state.ShuffleMode == PlaybackShuffleMode.ReshuffleEachLoop &&
            Queue.Count > 0 && !_pendingCycle.Any(p => p))
        {
            var oldOrder = Queue.ToArray();
            ShuffleQueue();
            // With two or more distinct upcoming songs, ensure the next pass changes.
            if (Queue.Count > 1 && Queue.SequenceEqual(oldOrder) && !_state.AvoidConsecutiveArtists && !_state.AvoidConsecutiveAlbums)
                Queue.Move(0, Queue.Count - 1);
            _currentPendingNextCycle = true;
        }
    }

    public void AdvanceQueue(bool recycleCurrent)
    {
        var previous = _state.CurrentTrack;
        var recycled = recycleCurrent && _state.RepeatMode == PlaybackRepeatMode.All && previous is not null;
        if (recycled)
            AddRecycledTrack(previous!);

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
        var index = _state.SelectedQueueIndex;
        var track = Queue[index];
        var preceding = Queue.Take(index).ToArray();
        for (var i = 0; i <= index; i++) Queue.RemoveAt(0);
        // Jump over occurrences without decoding them or recording listening plays.
        var previous = _state.CurrentTrack;
        var recycle = _state.RepeatMode == PlaybackRepeatMode.All;
        if (previous is not null)
        {
            if (recycle) AddRecycledTrack(previous);
            _history.Push((previous, recycle));
        }
        foreach (var skipped in preceding)
        {
            if (recycle) AddRecycledTrack(skipped);
            _history.Push((skipped, recycle));
        }
        _state.HistoryChanged();
        if (!_state.LoadTrack(track, playImmediately: true, rememberCurrent: false))
            AdvanceQueue(recycleCurrent: false);
    }

    public void JumpToHistory(int index)
    {
        var history = _history.Reverse().ToArray();
        if (index < 0 || index >= history.Length) return;
        _currentPendingNextCycle = false;
        var target = history[index].Track;
        var following = history.Skip(index + 1).Select(e => e.Track).ToList();
        if (_state.CurrentTrack is { } current) following.Add(current);
        // Remove repeat-all copies for every occurrence being returned to the front.
        // They may have been shuffled, so match from the tail rather than assuming a position.
        for (var i = history.Length - 1; i >= index; i--)
        {
            var entry = _history.Pop();
            if (!entry.Recycled) continue;
            var recycledIndex = Enumerable.Range(0, Queue.Count).Reverse().FirstOrDefault(j =>
                !_pendingCycle[j] && string.Equals(Queue[j].FilePath, entry.Track.FilePath, StringComparison.OrdinalIgnoreCase), -1);
            if (recycledIndex < 0)
                recycledIndex = Enumerable.Range(0, Queue.Count).Reverse().FirstOrDefault(j =>
                    string.Equals(Queue[j].FilePath, entry.Track.FilePath, StringComparison.OrdinalIgnoreCase), -1);
            if (recycledIndex >= 0) Queue.RemoveAt(recycledIndex);
        }
        for (var i = following.Count - 1; i >= 0; i--) Queue.Insert(0, following[i]);
        _state.HistoryChanged();
        if (!_state.LoadTrack(target, playImmediately: true, rememberCurrent: false))
            AdvanceQueue(recycleCurrent: false);
        _state.PreviousAvailabilityChanged();
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
