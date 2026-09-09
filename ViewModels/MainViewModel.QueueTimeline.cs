using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<QueueEntry> QueueTimeline { get; } = [];
    public bool IsQueueTimelineEmpty => QueueTimeline.Count == 0;
    private int _timelinePrefixCount;
    private bool _updatingTimeline;
    private QueueEntry? _nextQueueEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayQueueEntryCommand))]
    private QueueEntry? selectedQueueEntry;

    partial void OnSelectedQueueEntryChanged(QueueEntry? value)
    {
        if (_updatingTimeline) return;
        SelectedQueueIndex = value?.Kind == QueueEntryKind.Upcoming
            ? QueueTimeline.IndexOf(value) - _timelinePrefixCount : -1;
    }

    private void SelectUpcomingEntry()
    {
        if (_updatingTimeline) return;
        if (HasQueueSelection())
            SelectedQueueEntry = QueueTimeline[_timelinePrefixCount + SelectedQueueIndex];
        else if (SelectedQueueEntry?.Kind == QueueEntryKind.Upcoming)
            SelectedQueueEntry = null;
    }

    private bool HasTimelineSelection() => SelectedQueueEntry is not null;

    // The insertion point is between upcoming occurrences, before removing the dragged row.
    public void MoveQueueEntry(QueueEntry entry, int insertionIndex)
    {
        if (entry.Kind != QueueEntryKind.Upcoming || insertionIndex < 0 || insertionIndex > Queue.Count) return;
        var source = QueueTimeline.IndexOf(entry) - _timelinePrefixCount;
        if (source < 0 || source >= Queue.Count) return;
        var destination = insertionIndex > source ? insertionIndex - 1 : insertionIndex;
        if (source != destination) Queue.Move(source, destination);
        SelectedQueueEntry = entry;
    }

    [RelayCommand(CanExecute = nameof(HasTimelineSelection))]
    private void PlayQueueEntry()
    {
        if (SelectedQueueEntry is not { } entry) return;
        switch (entry.Kind)
        {
            case QueueEntryKind.Upcoming:
                PlayQueuedTrack();
                break;
            case QueueEntryKind.Current:
                Play();
                break;
            case QueueEntryKind.History:
                LoadTrack(entry.Track, playImmediately: true);
                break;
        }
    }

    private void OnTimelineQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selected = SelectedQueueEntry;
        _updatingTimeline = true;
        try
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    for (var i = 0; i < e.NewItems!.Count; i++)
                        QueueTimeline.Insert(_timelinePrefixCount + e.NewStartingIndex + i,
                            new QueueEntry((Track)e.NewItems[i]!, QueueEntryKind.Upcoming));
                    break;
                case NotifyCollectionChangedAction.Remove:
                    for (var i = 0; i < e.OldItems!.Count; i++)
                        QueueTimeline.RemoveAt(_timelinePrefixCount + e.OldStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Move:
                    QueueTimeline.Move(_timelinePrefixCount + e.OldStartingIndex, _timelinePrefixCount + e.NewStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    for (var i = 0; i < e.NewItems!.Count; i++)
                        QueueTimeline[_timelinePrefixCount + e.NewStartingIndex + i] =
                            new QueueEntry((Track)e.NewItems[i]!, QueueEntryKind.Upcoming);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    while (QueueTimeline.Count > _timelinePrefixCount)
                        QueueTimeline.RemoveAt(QueueTimeline.Count - 1);
                    foreach (var track in Queue)
                        QueueTimeline.Add(new QueueEntry(track, QueueEntryKind.Upcoming));
                    break;
            }
            SelectedQueueEntry = selected is not null && QueueTimeline.Contains(selected) ? selected : null;
        }
        finally { _updatingTimeline = false; }
        var next = Queue.Count > 0 ? QueueTimeline[_timelinePrefixCount] : null;
        if (!ReferenceEquals(next, _nextQueueEntry))
        {
            if (_nextQueueEntry is not null) _nextQueueEntry.IsNext = false;
            _nextQueueEntry = next;
            if (next is not null) next.IsNext = true;
        }
        OnSelectedQueueEntryChanged(SelectedQueueEntry);
        OnPropertyChanged(nameof(IsQueueTimelineEmpty));
        UpdateQueueCommands();
    }

    private void RefreshTimelineHistory()
    {
        var selected = SelectedQueueEntry;
        _updatingTimeline = true;
        try
        {
            for (var i = 0; i < _timelinePrefixCount; i++) QueueTimeline.RemoveAt(0);
            var prefix = _playbackHistory.Reverse()
                .Select(entry => new QueueEntry(entry.Track, QueueEntryKind.History)).ToList();
            if (CurrentTrack is { } track) prefix.Add(new QueueEntry(track, QueueEntryKind.Current));
            _timelinePrefixCount = prefix.Count;
            for (var i = 0; i < prefix.Count; i++) QueueTimeline.Insert(i, prefix[i]);
            SelectedQueueEntry = selected is not null && QueueTimeline.Contains(selected) ? selected : null;
        }
        finally { _updatingTimeline = false; }
        OnSelectedQueueEntryChanged(SelectedQueueEntry);
        OnPropertyChanged(nameof(IsQueueTimelineEmpty));
        if (_canSaveSession) _sessionDirty = true;
    }
}
