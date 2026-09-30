using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private LibrarySort librarySort;
    [ObservableProperty] private bool librarySortDescending;
    [ObservableProperty] private bool automaticScanning = true;
    [ObservableProperty] private WatchedMusicFolder? selectedWatchedFolder;
    [ObservableProperty] private string? settingsError;
    public IReadOnlyList<LibrarySort> LibrarySortOptions { get; } = Enum.GetValues<LibrarySort>();
    public ObservableCollection<WatchedMusicFolder> WatchedFolders { get; } = [];

    private bool _restoringLibraryPreferences;
    private void SaveLibraryWorkflowPreferences()
    {
        if (!_restoringLibraryPreferences) _uiPreferencesStore?.SaveLibraryWorkflow(
            new(LibrarySort, LibrarySortDescending, AutomaticScanning));
    }
    partial void OnLibrarySortChanged(LibrarySort value) { ApplyLibrarySort(); SaveLibraryWorkflowPreferences(); }
    partial void OnLibrarySortDescendingChanged(bool value) { ApplyLibrarySort(); SaveLibraryWorkflowPreferences(); }
    private void ApplyLibrarySort()
    {
        if (LibraryTracks is null) return;
        using (LibraryTracks.DeferRefresh())
        {
            LibraryTracks.SortDescriptions.Clear();
            var direction = LibrarySortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            void Add(string property) => LibraryTracks.SortDescriptions.Add(new(property, direction));
            Add(LibrarySort.ToString());
            if (LibrarySort == LibrarySort.Artist) Add(nameof(Track.Album));
            if (LibrarySort is LibrarySort.Artist or LibrarySort.Album)
            {
                Add(nameof(Track.DiscNumber));
                Add(nameof(Track.TrackNumber));
            }
            if (LibrarySort != LibrarySort.Title) Add(nameof(Track.Title));
            if (LibrarySort != LibrarySort.FilePath) Add(nameof(Track.FilePath));
        }
    }
    partial void OnAutomaticScanningChanged(bool value)
    {
        SaveLibraryWorkflowPreferences();
        if (value) { _refreshFallback?.Start(); ScheduleLibraryRefresh(); }
        else
        {
            _refreshFallback?.Stop();
            _refreshDebounce?.Stop();
            _fileMonitor?.Configure([]);
        }
    }
    private void RefreshWatchedFolders()
    {
        WatchedFolders.Clear();
        foreach (var folder in _musicFolders) WatchedFolders.Add(folder);
    }
    [RelayCommand(CanExecute = nameof(CanRefreshLibrary))]
    private async Task AddWatchedFolderAsync()
    {
        var path = _folderPickerService.PickMusicFolder();
        if (path is null) return;
        await RegisterMusicFolderAsync(path);
        RefreshWatchedFolders();
        await RefreshLibraryAsync(force: true);
    }
    [RelayCommand]
    private void RemoveWatchedFolder()
    {
        if (SelectedWatchedFolder is not { } folder || IsRefreshingLibrary || IsImportingPlaylist) return;
        try
        {
            _maintenanceStore?.RemoveMusicFolder(folder.Path);
            _musicFolders.RemoveAll(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase));
            SelectedWatchedFolder = null;
            RefreshWatchedFolders();
            _fileMonitor?.Configure(AutomaticScanning ? GetMusicFolders(Tracks) : []);
            SettingsError = null;
        }
        catch (Exception ex) { SettingsError = $"Could not remove watched folder: {ex.Message}"; }
    }

    public void PlayTracks(IEnumerable<Track> tracks)
    {
        var selected = tracks.ToArray();
        if (selected.Length > 0) StartPlaybackSession(selected);
    }

    public void QueueTracks(IEnumerable<Track> tracks, bool playNext = false)
    {
        var selected = tracks.ToArray();
        if (playNext)
            for (var i = selected.Length - 1; i >= 0; i--) Queue.Insert(0, selected[i]);
        else foreach (var track in selected) Queue.Add(track);
    }
    public bool TryAddTracksToPlaylist(IEnumerable<Track> tracks, Playlist playlist)
    {
        if (!CanChoosePlaylist || !Playlists.Contains(playlist)) return false;
        var added = tracks.ToArray();
        var original = playlist.Tracks.ToArray();
        playlist.AddTracks(added);
        SavePlaylists();
        if (PlaylistError is not null) { playlist.ReplaceTracks(original); return false; }
        PlaylistMessage = $"Added {added.Length} tracks to {playlist.Name}.";
        return true;
    }
    public void RemovePlaylistTracks(IEnumerable<int> indices)
    {
        if (!HasPlaylist()) return;
        var original = SelectedPlaylist!.Tracks.ToArray();
        foreach (var index in indices.Distinct().Where(i => i >= 0 && i < original.Length).OrderDescending())
            SelectedPlaylist.Tracks.RemoveAt(index);
        SelectedPlaylistTrackIndex = -1;
        SavePlaylists();
        if (PlaylistError is not null) SelectedPlaylist.ReplaceTracks(original);
    }
    public int[] MovePlaylistTracks(IEnumerable<int> indices, int direction)
    {
        if (!HasPlaylist() || direction is not (-1 or 1)) return [];
        var original = SelectedPlaylist!.Tracks.ToArray();
        var selected = indices.Where(i => i >= 0 && i < original.Length).ToHashSet();
        var ordered = direction < 0 ? selected.Order().ToArray() : selected.OrderDescending().ToArray();
        foreach (var index in ordered)
        {
            var target = index + direction;
            if (target < 0 || target >= original.Length || selected.Contains(target)) continue;
            SelectedPlaylist.Tracks.Move(index, target);
            selected.Remove(index);
            selected.Add(target);
        }
        SavePlaylists();
        if (PlaylistError is not null) { SelectedPlaylist.ReplaceTracks(original); return indices.ToArray(); }
        return selected.Order().ToArray();
    }
}
