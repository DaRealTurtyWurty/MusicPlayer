using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicPlayer.Models;

public enum QueueEntryKind { History, Current, Upcoming }

// Each row represents an occurrence, including when the same track appears twice.
public sealed class QueueEntry(Track track, QueueEntryKind kind) : ObservableObject
{
    public Track Track { get; } = track;
    public QueueEntryKind Kind { get; } = kind;
    public bool IsCurrent => Kind == QueueEntryKind.Current;
    public bool IsHistory => Kind == QueueEntryKind.History;
    private bool _isNext;
    public bool IsNext
    {
        get => _isNext;
        internal set
        {
            if (!SetProperty(ref _isNext, value)) return;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public string? Status => IsCurrent ? "Current song" : IsNext ? "Up next" : null;
    public bool HasStatus => IsCurrent || IsNext;
}
