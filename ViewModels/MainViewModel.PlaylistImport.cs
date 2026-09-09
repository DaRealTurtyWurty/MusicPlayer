using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private readonly IPlaylistImportService _playlistImportService;
    private int _importGeneration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportPlaylistFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportPlaylistFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddTracksToPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddMusicFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddMusicFolderCommand))]
    private bool isImportingPlaylist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressLabel))]
    private bool isAddingPlaylistTracks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressLabel))]
    [NotifyPropertyChangedFor(nameof(ImportProgressDetail))]
    private bool isFinishingPlaylistImport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsImportDiscovering))]
    [NotifyPropertyChangedFor(nameof(ImportProgressLabel))]
    [NotifyPropertyChangedFor(nameof(ImportProgressDetail))]
    private PlaylistImportProgress? importProgress;

    public bool IsImportDiscovering => ImportProgress?.Total is null;
    public double ImportProgressPercent => ImportProgress is { Total: > 0 } p ? 100d * p.Processed / p.Total.Value : 0;

    public string ImportProgressLabel => IsFinishingPlaylistImport
        ? (IsImportingLibrary ? "Saving library…" : "Saving playlist…")
        : ImportProgress is { Total: not null } p
            ? $"{(IsAddingPlaylistTracks || IsImportingLibrary ? "Adding tracks" : "Importing playlist")} · {p.Processed:N0} of {p.Total:N0} tracks"
            : $"Finding music · {ImportProgress?.Processed ?? 0:N0} entries found";

    public string ImportProgressDetail => IsFinishingPlaylistImport
        ? "Your tracks are ready. Finishing up…"
        : ImportProgress is { } p
            ? (p.CurrentFile ?? "Scanning files…") + (p.Skipped > 0 ? $" · {p.Skipped:N0} skipped" : "")
            : "Scanning files…";

    private bool CanImportPlaylist() => _canEditPlaylists && !IsImportingPlaylist && !IsRefreshingLibrary && !IsLocatingTrack;

    private bool CanAddTracksToPlaylist() => HasPlaylist() && !IsImportingPlaylist && !IsRefreshingLibrary && !IsLocatingTrack;

    [RelayCommand(CanExecute = nameof(CanAddTracksToPlaylist))]
    private async Task AddTracksToPlaylistAsync()
    {
        var playlist = SelectedPlaylist;
        if (playlist is null) return;
        var paths = _filePickerService.PickAudioFiles();
        if (paths.Count > 0)
            await ImportPlaylistAsync(progress => _playlistImportService.ImportTracksAsync(paths, progress), playlist);
    }

    [RelayCommand(CanExecute = nameof(CanImportPlaylist))]
    private async Task ImportPlaylistFileAsync()
    {
        var path = _filePickerService.PickPlaylistFile();
        if (path is not null)
            await ImportPlaylistAsync(progress => _playlistImportService.ImportFileAsync(path, progress));
    }

    [RelayCommand(CanExecute = nameof(CanImportPlaylist))]
    private async Task ImportPlaylistFolderAsync()
    {
        var path = _folderPickerService.PickMusicFolder();
        if (path is not null)
        {
            await ImportPlaylistAsync(progress => _playlistImportService.ImportFolderAsync(path, progress));
            await RegisterMusicFolderAsync(path, discoverNewTracks: false);
        }
    }

    private async Task ImportPlaylistAsync(Func<IProgress<PlaylistImportProgress>, Task<PlaylistImportResult>> import,
        Playlist? target = null)
    {
        IsAddingPlaylistTracks = target is not null;
        IsImportingPlaylist = true;
        IsFinishingPlaylistImport = false;
        var generation = ++_importGeneration;
        ImportProgress = new(0, null, 0, null);
        DismissToast();
        PlaylistError = null;
        try
        {
            var progress = new ImportProgressCallback(p =>
            {
                if (IsImportingPlaylist && !IsFinishingPlaylistImport && generation == _importGeneration)
                    ImportProgress = p;
            });
            var result = await import(progress);
            if (result.Tracks.Count == 0)
            {
                ShowToast(result.SkippedCount == 0
                    ? "No supported audio files found."
                    : $"No tracks {(target is null ? "imported" : "added")}. {result.SkippedCount} missing, unsupported, or unreadable entries skipped.");
                return;
            }

            IsFinishingPlaylistImport = true;
            var processed = result.Tracks.Count + result.SkippedCount;
            ImportProgress = new(processed, processed, result.SkippedCount, null);
            var existingTracks = target?.Tracks.ToArray() ?? [];
            var playlist = await Task.Run(() => new Playlist(existingTracks.Concat(result.Tracks))
            {
                Id = target?.Id ?? Guid.NewGuid(), Name = target?.Name ?? result.Name
            });
            // Snapshot UI-owned collections before saving on a worker.
            var savedPlaylists = Playlists.Select(p => p == target ? playlist
                : new Playlist(p.Tracks) { Id = p.Id, Name = p.Name }).ToList();
            if (target is null) savedPlaylists.Add(playlist);
            try
            {
                await Task.Run(() => _playlistStore?.Save(savedPlaylists));
                PlaylistMessage = null;
            }
            catch (Exception ex)
            {
                PlaylistError = $"Changes could not be saved: {ex.Message}";
            }

            if (target is null) Playlists.Add(playlist);
            else
            {
                var selectedIndex = SelectedPlaylistTrackIndex;
                target.AddTracks(result.Tracks);
                SelectedPlaylistTrackIndex = selectedIndex;
            }
            await SaveLibraryAsync();
            OpenPlaylist(target ?? playlist);
            SelectedPage = AppPage.Playlists;
            ShowToast((target is null ? $"Imported {playlist.Summary}."
                : $"Added {result.Tracks.Count} {(result.Tracks.Count == 1 ? "track" : "tracks")} to {target.Name}.") + (result.SkippedCount > 0
                ? $" Skipped {result.SkippedCount} missing, unsupported, or unreadable entries."
                : ""));
        }
        catch (Exception ex)
        {
            PlaylistError = $"{(target is null ? "Could not import playlist" : "Could not add tracks")}: {ex.Message}";
        }
        finally
        {
            IsImportingPlaylist = false;
            IsFinishingPlaylistImport = false;
            IsAddingPlaylistTracks = false;
        }
    }

    private sealed class ImportProgressCallback(Action<PlaylistImportProgress> callback)
        : IProgress<PlaylistImportProgress>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(PlaylistImportProgress value)
        {
            if (_context is null) callback(value);
            else _context.Post(_ => callback(value), null);
        }
    }
}
