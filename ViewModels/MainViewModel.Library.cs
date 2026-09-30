using System.Collections.Specialized;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels.Coordination;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string librarySearchText = "";
    [ObservableProperty] private bool showUncategorizedTracks;
    [ObservableProperty] private string? libraryError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressLabel))]
    private bool isImportingLibrary;

    public ListCollectionView LibraryTracks { get; private set; } = null!;

    private void InitializeLibrary()
    {
        // A dedicated view keeps browsing filters separate from the source library and queue.
        LibraryTracks = new ListCollectionView(Tracks) { Filter = MatchesLibrarySearch };
        var preferences = _uiPreferencesStore?.LoadLibraryWorkflow() ?? new();
        _restoringLibraryPreferences = true;
        LibrarySort = Enum.IsDefined(preferences.Sort) ? preferences.Sort : LibrarySort.Artist;
        LibrarySortDescending = preferences.Descending;
        AutomaticScanning = preferences.AutomaticScanning;
        _restoringLibraryPreferences = false;
        ApplyLibrarySort();
        ((INotifyCollectionChanged)LibraryTracks).CollectionChanged += OnLibraryViewCollectionChanged;
        _library.Load();
    }

    // Library visibility follows explicit additions and the current playlist memberships.
    private void InitializeLibraryPlaylists() => _library.InitializePlaylists();

    private void RefreshPlaylistMembership() => _library.RefreshPlaylistMembership();

    private static string LibraryTrackKey(string path) => LibraryCoordinator.LibraryTrackKey(path);

    private int AddLibraryTracks(IEnumerable<Track> tracks, bool save = true, bool explicitlyAdded = false) =>
        _library.AddTracks(tracks, save, explicitlyAdded);

    private void MarkTracksExplicit(IEnumerable<Track> tracks, bool savePending = true) => _library.MarkTracksExplicit(tracks, savePending);

    private void SaveLibrary() => _library.Save();

    private Task SaveLibraryAsync() => _library.SaveAsync();

    private bool CanImportLibrary() => _library.CanSave && !IsImportingPlaylist && !IsRefreshingLibrary && !IsLocatingTrack;

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
        if (!_library.SavePending) LibraryError = null;
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
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Music import failed: {ex}"); LibraryError = $"Could not add music: {ex.Message}"; }
        finally
        {
            IsImportingPlaylist = false;
            IsFinishingPlaylistImport = false;
            IsImportingLibrary = false;
        }
    }

    private void OnLibraryViewCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        PlayAllCommand.NotifyCanExecuteChanged();

    private void DisposeLibrary()
    {
        ((INotifyCollectionChanged)LibraryTracks).CollectionChanged -= OnLibraryViewCollectionChanged;
        _library.Dispose();
    }

    private bool MatchesLibrarySearch(object item)
    {
        if (item is not Track track) return false;
        if (ShowUncategorizedTracks && _library.IsPlaylistTrack(track)) return false;
        return TrackSearch.Matches(track, LibrarySearchText);
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
