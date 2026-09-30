using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels.Coordination;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private IListeningHistoryStore? _listeningHistoryStore;
    private readonly Dictionary<string, long> _playCounts = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ListeningHistoryEntry> ListeningHistory { get; } = [];
    [ObservableProperty] private string? listeningHistoryError;

    private void InitializeListeningHistory(IListeningHistoryStore? store)
    {
        _listeningHistoryStore = store;
        RefreshListeningHistory();
        Tracks.CollectionChanged += OnListeningTracksChanged;
    }

    [RelayCommand]
    private void RefreshListeningHistory()
    {
        if (_listeningHistoryStore is null) return;
        try
        {
            var history = _listeningHistoryStore.LoadListeningHistory();
            var counts = _listeningHistoryStore.LoadPlayCounts();
            _playCounts.Clear();
            foreach (var pair in counts) _playCounts[pair.Key] = pair.Value;
            ListeningHistory.Clear();
            foreach (var entry in history) ListeningHistory.Add(entry);
            UpdateListeningCounts();
            ListeningHistoryError = null;
        }
        catch (Exception ex) { ListeningHistoryError = $"Could not load listening history: {ex.Message}"; }
    }

    private void ApplyPlayCount(Track track) => track.PlayCount = _playCounts.GetValueOrDefault(LibraryTrackKey(track.FilePath));
    private void UpdateListeningCounts()
    {
        foreach (var track in KnownPlaybackTracks().Concat(ListeningHistory.Select(e => e.Track))) ApplyPlayCount(track);
        if (LibrarySort == LibrarySort.PlayCount) LibraryTracks.Refresh();
    }
    private void OnListeningTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var track in e.NewItems?.Cast<Track>() ?? (e.Action == NotifyCollectionChangedAction.Reset ? Tracks : [])) ApplyPlayCount(track);
    }

    void IPlaybackState.ListeningStarted(Track track)
    {
        try
        {
            var entry = _listeningHistoryStore?.RecordListen(track, DateTimeOffset.UtcNow)
                ?? new ListeningHistoryEntry(0, track, DateTimeOffset.UtcNow);
            var key = LibraryTrackKey(track.FilePath);
            _playCounts[key] = _listeningHistoryStore is null ? _playCounts.GetValueOrDefault(key) + 1 : entry.Track.PlayCount;
            ListeningHistory.Insert(0, entry);
            if (ListeningHistory.Count > 500) ListeningHistory.RemoveAt(ListeningHistory.Count - 1);
            UpdateListeningCounts();
            ListeningHistoryError = null;
        }
        catch (Exception ex)
        {
            ListeningHistoryError = $"Could not save listening history: {ex.Message}";
            System.Diagnostics.Trace.TraceWarning(ListeningHistoryError);
        }
    }

    [RelayCommand]
    private void PlayHistoryEntry(ListeningHistoryEntry? entry)
    {
        if (entry is not null) LoadTrack(entry.Track, playImmediately: true);
    }
}
