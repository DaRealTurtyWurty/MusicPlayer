using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels.Coordination;

internal sealed class LibraryTrackCollection : ObservableCollection<Track>
{
    protected override void InsertItem(int index, Track item)
    {
        // Direct additions represent adding to the library; playlist seeding uses AddRange.
        item.ExplicitlyAddedToLibrary = true;
        base.InsertItem(index, item);
    }
    public void ReplaceAll(IReadOnlyList<Track> tracks)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var track in tracks) Items.Add(track);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
    public void AddRange(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        CheckReentrancy();
        foreach (var track in tracks) Items.Add(track);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
