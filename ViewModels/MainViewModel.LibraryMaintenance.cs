using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private ILibraryRefreshService? _libraryRefreshService;
    private ILibraryMaintenanceStore? _maintenanceStore;
    private ITrackMatchPicker? _trackMatchPicker;
    private readonly CancellationTokenSource _maintenanceCancellation = new();
    private readonly List<WatchedMusicFolder> _musicFolders = [];
    private DispatcherTimer? _refreshDebounce;
    private DispatcherTimer? _refreshFallback;
    private LibraryFileMonitor? _fileMonitor;
    private bool _applyingTrackUpdates;
    private bool _reloadCurrentTrack;
    private DispatcherTimer? _toastTimer;
    private bool _isRelinkingTrack;
    private bool _playlistSaveDeferred;

    [ObservableProperty] private bool isRefreshingLibrary;
    [ObservableProperty] private bool isLocatingTrack;
    [ObservableProperty] private string? libraryRefreshStatus;
    [ObservableProperty] private string? locateStatus;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string? toastMessage;
    public bool HasToast => ToastMessage is not null;

    private void InitializeLibraryMaintenance(ILibraryRefreshService? service, ITrackMatchPicker? matchPicker, bool monitor)
    {
        _libraryRefreshService = service;
        _trackMatchPicker = matchPicker;
        _maintenanceStore = _libraryStore as ILibraryMaintenanceStore;
        if (service is null || !_canSaveLibrary) return;
        try { _musicFolders.AddRange(_maintenanceStore?.LoadMusicFolders() ?? []); }
        catch (Exception ex) { LibraryError = $"Could not load watched folders: {ex.Message}"; }
        if (!monitor) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _refreshDebounce.Tick += OnRefreshRequested;
        // Incremental reconciliation also covers watcher overflows and disconnected drives returning.
        _refreshFallback = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshFallback.Tick += OnRefreshRequested;
        _refreshFallback.Start();
        _fileMonitor = new LibraryFileMonitor(() =>
        {
            if (!_maintenanceCancellation.IsCancellationRequested && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(ScheduleLibraryRefresh);
        });
        ScheduleLibraryRefresh();
    }

    private void ScheduleLibraryRefresh()
    {
        if (_maintenanceCancellation.IsCancellationRequested || _refreshDebounce is null) return;
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        _refreshDebounce?.Stop();
        await RefreshLibraryAsync();
    }

    private bool CanRefreshLibrary() => _libraryRefreshService is not null && _canSaveLibrary &&
        !IsRefreshingLibrary && !IsLocatingTrack && !IsImportingPlaylist;

    [RelayCommand(CanExecute = nameof(CanRefreshLibrary))]
    private Task RescanLibraryAsync() => RefreshLibraryAsync(force: true);

    public async Task RefreshLibraryAsync(bool force = false)
    {
        if (_maintenanceCancellation.IsCancellationRequested || _libraryRefreshService is null) return;
        if (!CanRefreshLibrary()) { ScheduleLibraryRefresh(); return; }
        IsRefreshingLibrary = true;
        LibraryRefreshStatus = "Refreshing library…";
        try
        {
            var tracks = KnownPlaybackTracks().DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase).ToArray();
            var folders = GetMusicFolders(tracks);
            if (_fileMonitor is { } monitor) await Task.Run(() => monitor.Configure(folders));
            var result = await _libraryRefreshService.RefreshAsync(tracks, folders, force, _maintenanceCancellation.Token);
            _maintenanceCancellation.Token.ThrowIfCancellationRequested();
            ApplyTrackUpdates(result.Updates.ToDictionary(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase));
            var added = AddLibraryTracks(result.Added, save: false, explicitlyAdded: true);
            if (result.Updates.Count > 0 || added > 0) _librarySavePending = true;
            // Retain the pending flag after a failed write so a later scan retries it.
            await SaveLibraryAsync();
            var missing = Tracks.Count(t => t.IsMissing);
            LibraryRefreshStatus = $"Library up to date · {result.Updates.Count} refreshed · {added} added" +
                (missing > 0 ? $" · {missing} missing" : "") +
                (result.Failed > 0 ? $" · {result.Failed} files or folders could not be read; they will be retried" : "");
            if (force) ShowToast(LibraryError ?? $"Scan complete · {result.Updates.Count} refreshed · {added} added" +
                (missing > 0 ? $" · {missing} missing" : "") + (result.Failed > 0 ? $" · {result.Failed} could not be read" : ""));
            if (_fileMonitor is { } updatedMonitor)
            {
                var updatedFolders = GetMusicFolders(Tracks);
                await Task.Run(() => updatedMonitor.Configure(updatedFolders));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LibraryRefreshStatus = $"Could not refresh library: {ex.Message}";
            if (force) ShowToast(LibraryRefreshStatus);
        }
        finally { IsRefreshingLibrary = false; }
    }

    private IReadOnlyList<WatchedMusicFolder> GetMusicFolders(IEnumerable<Track> tracks)
    {
        var trackFolders = tracks.GroupBy(t => Path.GetDirectoryName(Path.GetFullPath(t.FilePath)), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key is not null).Select(g => new WatchedMusicFolder(g.Key!, false, g.Any(t => t.ExplicitlyAddedToLibrary)));
        var folders = _musicFolders.Concat(trackFolders)
            .GroupBy(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)), StringComparer.OrdinalIgnoreCase)
            .Select(g => new WatchedMusicFolder(g.Key, g.Any(f => f.IncludeSubdirectories), g.Any(f => f.DiscoverNewTracks))).ToArray();
        return folders.Where(f => !folders.Any(parent => parent != f && parent.IncludeSubdirectories &&
            (parent.DiscoverNewTracks || !f.DiscoverNewTracks) &&
            f.Path.StartsWith(Path.EndsInDirectorySeparator(parent.Path) ? parent.Path : parent.Path + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private async Task RegisterMusicFolderAsync(string path, bool discoverNewTracks = true)
    {
        if (_libraryRefreshService is null) return;
        var folder = new WatchedMusicFolder(Path.GetFullPath(path), true, discoverNewTracks);
        if (!_musicFolders.Contains(folder)) _musicFolders.Add(folder);
        try { await Task.Run(() => _maintenanceStore?.SaveMusicFolder(folder)); }
        catch (Exception ex) { LibraryError = $"Could not save watched folder: {ex.Message}"; }
        ScheduleLibraryRefresh();
    }

    private bool CanLocateTrack(Track? track) => track?.IsMissing == true && CanRefreshLibrary();

    [RelayCommand(CanExecute = nameof(CanLocateTrack))]
    private async Task LocateFileAsync(Track? track)
    {
        if (!CanLocateTrack(track)) return;
        var path = _filePickerService.PickAudioFile();
        if (path is not null) await LocateTrackAsync(track!, path, searchFolder: false);
    }

    [RelayCommand(CanExecute = nameof(CanLocateTrack))]
    private async Task LocateFolderAsync(Track? track)
    {
        if (!CanLocateTrack(track)) return;
        var path = _folderPickerService.PickMusicFolder();
        if (path is not null) await LocateTrackAsync(track!, path, searchFolder: true);
    }

    private async Task LocateTrackAsync(Track missing, string path, bool searchFolder)
    {
        IsLocatingTrack = true;
        LocateStatus = searchFolder ? $"Searching for {missing.Title}…" : $"Locating {missing.Title}…";
        try
        {
            Track? replacement;
            if (searchFolder)
            {
                if (string.IsNullOrWhiteSpace(missing.Artist))
                {
                    LocateStatus = "This song has no saved artist to match. Use Locate → Choose file instead.";
                    return;
                }
                var result = await _libraryRefreshService!.FindMatchesAsync(missing, path, _maintenanceCancellation.Token);
                _maintenanceCancellation.Token.ThrowIfCancellationRequested();
                if (result.Matches.Count == 0)
                {
                    LocateStatus = "No matching title and artist found in that folder or its subfolders." +
                        (result.Failed > 0 ? $" {result.Failed} files or folders could not be read." : "");
                    return;
                }
                replacement = result.Matches.Count == 1 ? result.Matches[0] : _trackMatchPicker?.PickMatch(missing, result.Matches);
                if (replacement is null) { LocateStatus = "Locate cancelled; the original track was kept."; return; }
                // The user may leave the candidate dialog open while files change.
                replacement = await _libraryRefreshService.ReadFileAsync(replacement.FilePath, _maintenanceCancellation.Token);
                if (!LibraryRefreshService.MetadataMatches(missing, replacement))
                    throw new IOException("The matching file's tags changed during the search. Please search again.");
            }
            else replacement = await _libraryRefreshService!.ReadFileAsync(path, _maintenanceCancellation.Token);
            _maintenanceCancellation.Token.ThrowIfCancellationRequested();
            _isRelinkingTrack = true;
            if (_maintenanceStore is not null)
                await Task.Run(() => _maintenanceStore.RelocateTrack(missing.FilePath, replacement));
            var currentRelocated = CurrentTrack is { } current &&
                LibraryTrackKey(current.FilePath).Equals(LibraryTrackKey(missing.FilePath), StringComparison.OrdinalIgnoreCase);
            ApplyTrackUpdates(new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase)
            {
                [LibraryTrackKey(missing.FilePath)] = replacement,
                [LibraryTrackKey(replacement.FilePath)] = replacement
            });
            if (currentRelocated) _reloadCurrentTrack = true;
            LocateStatus = $"Located {replacement.Title}.";
            ScheduleLibraryRefresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocateStatus = $"Could not locate {missing.Title}: {ex.Message}"; }
        finally
        {
            _isRelinkingTrack = false;
            if (_playlistSaveDeferred)
            {
                _playlistSaveDeferred = false;
                SavePlaylists();
            }
            await SaveLibraryAsync();
            IsLocatingTrack = false;
        }
    }

    [RelayCommand]
    private void DismissLocateStatus() => LocateStatus = null;

    private void ShowToast(string message)
    {
        if (_maintenanceCancellation.IsCancellationRequested) return;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastExpired;
        _toastTimer.Tick += OnToastExpired;
        ToastMessage = message;
        _toastTimer.Start();
    }

    private void OnToastExpired(object? sender, EventArgs e) => DismissToast();

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer?.Stop();
        ToastMessage = null;
    }

    private IEnumerable<Track> KnownPlaybackTracks() => _trackCatalog.Values.Concat(Tracks).Concat(Playlists.SelectMany(p => p.Tracks)).Concat(Queue)
        .Concat(_playbackHistory.Select(e => e.Track)).Concat(CurrentTrack is { } current ? [current] : []);

    private void ApplyTrackUpdates(IReadOnlyDictionary<string, Track> updates)
    {
        if (updates.Count == 0) return;
        foreach (var (oldPath, replacement) in updates)
        {
            if (_trackCatalog.TryGetValue(oldPath, out var original))
                replacement.ExplicitlyAddedToLibrary |= original.ExplicitlyAddedToLibrary;
            var newPath = LibraryTrackKey(replacement.FilePath);
            if (_trackCatalog.TryGetValue(newPath, out var destination))
                replacement.ExplicitlyAddedToLibrary |= destination.ExplicitlyAddedToLibrary;
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) _trackCatalog.Remove(oldPath);
            _trackCatalog[newPath] = replacement;
        }
        Track Replace(Track track) => updates.TryGetValue(LibraryTrackKey(track.FilePath), out var updated) ? updated : track;
        var selected = SelectedTrack is { } selection ? Replace(selection) : null;
        var queueIndex = SelectedQueueIndex;
        var playlistIndex = SelectedPlaylistTrackIndex;
        _applyingTrackUpdates = true;
        try
        {
            ((LibraryTrackCollection)Tracks).ReplaceAll(Tracks.Select(Replace)
                .DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase).ToArray());
            foreach (var playlist in Playlists)
            {
                var replacement = playlist.Tracks.Select(Replace).ToArray();
                if (!playlist.Tracks.SequenceEqual(replacement)) playlist.ReplaceTracks(replacement);
            }
            for (var i = 0; i < Queue.Count; i++)
                if (!ReferenceEquals(Queue[i], Replace(Queue[i]))) Queue[i] = Replace(Queue[i]);
            var history = _playbackHistory.Reverse().Select(e => (Track: Replace(e.Track), e.Recycled)).ToArray();
            _playbackHistory.Clear();
            foreach (var entry in history) _playbackHistory.Push(entry);
            if (CurrentTrack is { } current) CurrentTrack = Replace(current);
            RefreshTimelineHistory();
            SelectedTrack = selected;
            SelectedQueueIndex = queueIndex;
            SelectedPlaylistTrackIndex = playlistIndex;
            _playlistTrackPaths = Playlists.SelectMany(p => p.Tracks).Select(t => LibraryTrackKey(t.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            LibraryTracks.Refresh();
        }
        finally { _applyingTrackUpdates = false; }
        if (_canSaveSession) _sessionDirty = true;
        UpdateMaintenanceCommands();
    }

    partial void OnIsRefreshingLibraryChanged(bool value) => UpdateMaintenanceCommands();
    partial void OnIsLocatingTrackChanged(bool value) => UpdateMaintenanceCommands();
    partial void OnIsImportingPlaylistChanged(bool value) => UpdateMaintenanceCommands();

    private void UpdateMaintenanceCommands()
    {
        ConfirmDeletePlaylistCommand.NotifyCanExecuteChanged();
        RescanLibraryCommand.NotifyCanExecuteChanged();
        LocateFileCommand.NotifyCanExecuteChanged();
        LocateFolderCommand.NotifyCanExecuteChanged();
        AddMusicFilesCommand.NotifyCanExecuteChanged();
        AddMusicFolderCommand.NotifyCanExecuteChanged();
        ImportPlaylistFileCommand.NotifyCanExecuteChanged();
        ImportPlaylistFolderCommand.NotifyCanExecuteChanged();
        AddTracksToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private void DisposeLibraryMaintenance()
    {
        _maintenanceCancellation.Cancel();
        _fileMonitor?.Dispose();
        _refreshDebounce?.Stop();
        _refreshFallback?.Stop();
        _toastTimer?.Stop();
    }
}
