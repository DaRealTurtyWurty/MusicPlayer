using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string librarySearchText = "";
    [ObservableProperty] private bool showUncategorizedTracks;
    [ObservableProperty] private string? libraryError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressLabel))]
    private bool isImportingLibrary;

    private ILibraryStore? _libraryStore;
    private bool _canSaveLibrary = true;
    private bool _librarySavePending;
    private readonly HashSet<Playlist> _libraryPlaylists = [];
    private HashSet<string> _playlistTrackPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Track> _trackCatalog = new(StringComparer.OrdinalIgnoreCase);

    public ListCollectionView LibraryTracks { get; private set; } = null!;

    private void InitializeLibrary(ILibraryStore? store)
    {
        _libraryStore = store;
        // A dedicated view keeps browsing filters separate from the source library and queue.
        LibraryTracks = new ListCollectionView(Tracks) { Filter = MatchesLibrarySearch };
        LibraryTracks.SortDescriptions.Add(new(nameof(Track.Artist), ListSortDirection.Ascending));
        LibraryTracks.SortDescriptions.Add(new(nameof(Track.Album), ListSortDirection.Ascending));
        LibraryTracks.SortDescriptions.Add(new(nameof(Track.Title), ListSortDirection.Ascending));
        ((INotifyCollectionChanged)LibraryTracks).CollectionChanged += (_, _) => PlayAllCommand.NotifyCanExecuteChanged();
        Tracks.CollectionChanged += OnLibraryCollectionChanged;
        try
        {
            var visible = store?.Load() ?? [];
            RegisterKnownTracks(store is ILibraryMembershipStore membership ? membership.LoadKnownTracks() : visible);
            AddLibraryTracks(visible, save: false);
        }
        catch (Exception ex)
        {
            _canSaveLibrary = false;
            LibraryError = $"Could not load saved library: {ex.Message}";
        }
    }

    // Library visibility follows explicit additions and the current playlist memberships.
    private void InitializeLibraryPlaylists()
    {
        Playlists.CollectionChanged += OnLibraryPlaylistsChanged;
        SynchronizeLibraryPlaylists();
    }

    private void OnLibraryPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeLibraryPlaylists();

    private void SynchronizeLibraryPlaylists()
    {
        foreach (var removed in _libraryPlaylists.Where(p => !Playlists.Contains(p)).ToArray())
        {
            removed.Tracks.CollectionChanged -= OnLibraryPlaylistTracksChanged;
            _libraryPlaylists.Remove(removed);
        }
        foreach (var playlist in Playlists)
            if (_libraryPlaylists.Add(playlist))
                playlist.Tracks.CollectionChanged += OnLibraryPlaylistTracksChanged;
        RefreshPlaylistMembership();
    }

    private void OnLibraryPlaylistTracksChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPlaylistMembership();

    private void RefreshPlaylistMembership()
    {
        if (_applyingTrackUpdates) return;
        var tracks = Playlists.SelectMany(p => p.Tracks).ToArray();
        _playlistTrackPaths = tracks.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddLibraryTracks(tracks);
        var retained = Tracks.Where(t => t.ExplicitlyAddedToLibrary || _playlistTrackPaths.Contains(LibraryTrackKey(t.FilePath))).ToArray();
        if (retained.Length != Tracks.Count)
        {
            var selected = SelectedTrack;
            ((LibraryTrackCollection)Tracks).ReplaceAll(retained);
            SelectedTrack = selected is not null && retained.Contains(selected) ? selected : null;
        }
        LibraryTracks.Refresh();
        OnPropertyChanged(nameof(DeletePlaylistLibraryImpact));
        OnPropertyChanged(nameof(PlaylistExclusiveSongCount));
        OnPropertyChanged(nameof(HasPlaylistExclusiveSongs));
    }

    private static string LibraryTrackKey(string path) => Path.GetFullPath(path);

    private int AddLibraryTracks(IEnumerable<Track> tracks, bool save = true, bool explicitlyAdded = false)
    {
        var incoming = tracks.ToArray();
        if (explicitlyAdded) MarkTracksExplicit(incoming);
        RegisterKnownTracks(incoming);
        var known = Tracks.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = incoming.Where(t => known.Add(LibraryTrackKey(t.FilePath)))
            .Select(t => _trackCatalog[LibraryTrackKey(t.FilePath)]).ToArray();
        ((LibraryTrackCollection)Tracks).AddRange(added);
        if (save && added.Length > 0) _librarySavePending = true;
        if (save && !IsImportingPlaylist) SaveLibrary();
        if (save && added.Length > 0) ScheduleLibraryRefresh();
        return added.Length;
    }

    private void RegisterKnownTracks(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
        {
            var key = LibraryTrackKey(track.FilePath);
            if (_trackCatalog.TryGetValue(key, out var known))
            {
                known.ExplicitlyAddedToLibrary |= track.ExplicitlyAddedToLibrary;
                track.ExplicitlyAddedToLibrary |= known.ExplicitlyAddedToLibrary;
            }
            else _trackCatalog.Add(key, track);
        }
    }

    private void OnLibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RegisterKnownTracks(e.NewItems?.Cast<Track>() ?? (e.Action == NotifyCollectionChangedAction.Reset ? Tracks : []));

    private void MarkTracksExplicit(IEnumerable<Track> tracks, bool savePending = true)
    {
        var incoming = tracks.ToArray();
        var paths = incoming.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var track in KnownPlaybackTracks().Concat(incoming))
            if (paths.Contains(LibraryTrackKey(track.FilePath))) track.ExplicitlyAddedToLibrary = true;
        if (savePending && paths.Count > 0) _librarySavePending = true;
        OnPropertyChanged(nameof(DeletePlaylistLibraryImpact));
        OnPropertyChanged(nameof(PlaylistExclusiveSongCount));
        OnPropertyChanged(nameof(HasPlaylistExclusiveSongs));
    }

    private void SaveLibrary()
    {
        if (!_canSaveLibrary || !_librarySavePending || _isRelinkingTrack) return; // Preserve unreadable storage for recovery.
        try
        {
            _libraryStore?.Save(KnownPlaybackTracks().DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase));
            _librarySavePending = false;
            LibraryError = null;
        }
        catch (Exception ex) { LibraryError = $"Library changes could not be saved: {ex.Message}"; }
    }

    private async Task SaveLibraryAsync()
    {
        if (!_canSaveLibrary || !_librarySavePending || _isRelinkingTrack) return;
        var snapshot = KnownPlaybackTracks().DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            await Task.Run(() => _libraryStore?.Save(snapshot));
            _librarySavePending = false;
            LibraryError = null;
        }
        catch (Exception ex) { LibraryError = $"Library changes could not be saved: {ex.Message}"; }
    }

    private bool CanImportLibrary() => _canSaveLibrary && !IsImportingPlaylist && !IsRefreshingLibrary && !IsLocatingTrack;

    [RelayCommand(CanExecute = nameof(CanImportLibrary))]
    private async Task AddMusicFilesAsync()
    {
        var paths = _filePickerService.PickAudioFiles();
        if (paths.Count > 0)
            await ImportLibraryAsync(progress => _playlistImportService.ImportTracksAsync(paths, progress));
    }

    [RelayCommand(CanExecute = nameof(CanImportLibrary))]
    private async Task AddMusicFolderAsync()
    {
        var path = _folderPickerService.PickMusicFolder();
        if (path is not null)
        {
            await ImportLibraryAsync(progress => _playlistImportService.ImportFolderAsync(path, progress));
            await RegisterMusicFolderAsync(path);
        }
    }

    private async Task ImportLibraryAsync(Func<IProgress<PlaylistImportProgress>, Task<PlaylistImportResult>> import)
    {
        IsImportingLibrary = true;
        IsImportingPlaylist = true;
        IsFinishingPlaylistImport = false;
        var generation = ++_importGeneration;
        ImportProgress = new(0, null, 0, null);
        DismissToast();
        if (!_librarySavePending) LibraryError = null;
        try
        {
            var result = await import(new ImportProgressCallback(p =>
            {
                if (IsImportingLibrary && !IsFinishingPlaylistImport && generation == _importGeneration)
                    ImportProgress = p;
            }));
            IsFinishingPlaylistImport = true;
            var processed = result.Tracks.Count + result.SkippedCount;
            ImportProgress = new(processed, processed, result.SkippedCount, null);
            var added = AddLibraryTracks(result.Tracks, explicitlyAdded: true);
            await SaveLibraryAsync();
            var duplicates = result.Tracks.Count - added;
            var message = result.Tracks.Count == 0
                ? "No supported music found."
                : $"Added {added} {(added == 1 ? "track" : "tracks")} to your library.";
            if (duplicates > 0) message += $" {duplicates} already in your library.";
            if (result.SkippedCount > 0) message += $" Skipped {result.SkippedCount} missing, unsupported, or unreadable entries.";
            ShowToast(message);
        }
        catch (Exception ex) { LibraryError = $"Could not add music: {ex.Message}"; }
        finally
        {
            IsImportingPlaylist = false;
            IsFinishingPlaylistImport = false;
            IsImportingLibrary = false;
        }
    }

    private void DisposeLibrary()
    {
        Tracks.CollectionChanged -= OnLibraryCollectionChanged;
        Playlists.CollectionChanged -= OnLibraryPlaylistsChanged;
        foreach (var playlist in _libraryPlaylists)
            playlist.Tracks.CollectionChanged -= OnLibraryPlaylistTracksChanged;
    }

    private sealed class LibraryTrackCollection : ObservableCollection<Track>
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

    private bool MatchesLibrarySearch(object item)
    {
        if (item is not Track track) return false;
        if (ShowUncategorizedTracks && _playlistTrackPaths.Contains(LibraryTrackKey(track.FilePath))) return false;
        var query = LibrarySearchText.Trim();
        return query.Length == 0 ||
               track.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (track.Artist?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
               (track.Album?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    partial void OnLibrarySearchTextChanged(string value) => LibraryTracks.Refresh();
    partial void OnShowUncategorizedTracksChanged(bool value) => LibraryTracks.Refresh();

    [RelayCommand]
    private void ClearLibrarySearch() => LibrarySearchText = "";

    [RelayCommand]
    private void ClearLibraryFilters()
    {
        LibrarySearchText = "";
        ShowUncategorizedTracks = false;
    }
}
