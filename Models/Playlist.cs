using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicPlayer.Models;

public partial class Playlist : ObservableObject
{
    private IReadOnlyList<Track> _coverTracks = [];

    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty] private string name = "New playlist";

    public ObservableCollection<Track> Tracks { get; }
    public string Summary => $"{Tracks.Count} {(Tracks.Count == 1 ? "track" : "tracks")}";

    public string DurationSummary
    {
        get
        {
            var duration = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
            var time = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours} hr {duration.Minutes} min"
                : $"{(int)duration.TotalMinutes} min";
            return Tracks.Count == 0 ? Summary : $"{Summary} · {time}";
        }
    }

    public IReadOnlyList<Track> CoverTracks => _coverTracks;

    public Playlist() : this([])
    {
    }

    public Playlist(IEnumerable<Track> tracks)
    {
        Tracks = new PlaylistTracks(tracks);
        RefreshCoverTracks();
        Tracks.CollectionChanged += (_, _) =>
        {
            RefreshCoverTracks();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(DurationSummary));
            OnPropertyChanged(nameof(CoverTracks));
        };
    }

    private void RefreshCoverTracks() => _coverTracks =
        Tracks.DistinctBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();

    public void AddTracks(IEnumerable<Track> tracks) => ((PlaylistTracks)Tracks).AddRange(tracks);

    public void ReplaceTracks(IEnumerable<Track> tracks) => ((PlaylistTracks)Tracks).ReplaceAll(tracks);

    private sealed class PlaylistTracks(IEnumerable<Track> tracks) : ObservableCollection<Track>(tracks)
    {
        public void ReplaceAll(IEnumerable<Track> tracks)
        {
            var replacement = tracks.ToArray();
            CheckReentrancy();
            Items.Clear();
            AddRange(replacement);
        }
        public void AddRange(IEnumerable<Track> tracks)
        {
            var added = tracks.ToArray();
            if (added.Length == 0) return;
            CheckReentrancy();
            foreach (var track in added) Items.Add(track);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }
}
