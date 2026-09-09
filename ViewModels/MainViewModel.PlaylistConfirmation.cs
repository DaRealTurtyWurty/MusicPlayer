using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeletePlaylistConfirmationOpen))]
    [NotifyPropertyChangedFor(nameof(PlaylistExclusiveSongCount))]
    [NotifyPropertyChangedFor(nameof(HasPlaylistExclusiveSongs))]
    [NotifyPropertyChangedFor(nameof(DeletePlaylistLibraryImpact))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeletePlaylistCommand))]
    private Playlist? playlistPendingDeletion;

    public bool IsDeletePlaylistConfirmationOpen => PlaylistPendingDeletion is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletePlaylistLibraryImpact))]
    private bool keepPlaylistSongsInLibrary;

    [ObservableProperty] private string? playlistDeletionError;

    public int PlaylistExclusiveSongCount => GetPlaylistExclusiveSongs().Length;
    public bool HasPlaylistExclusiveSongs => PlaylistExclusiveSongCount > 0;
    public string DeletePlaylistLibraryImpact => PlaylistExclusiveSongCount switch
    {
        0 => "All songs will stay in your library.",
        var count when KeepPlaylistSongsInLibrary => $"{count} {(count == 1 ? "song will" : "songs will")} be kept in your library.",
        var count => $"{count} {(count == 1 ? "song will" : "songs will")} also leave your library."
    };

    private Track[] GetPlaylistExclusiveSongs()
    {
        if (PlaylistPendingDeletion is not { } playlist) return [];
        var shared = Playlists.Where(p => p != playlist).SelectMany(p => p.Tracks)
            .Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return playlist.Tracks.DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase)
            .Where(t => !shared.Contains(LibraryTrackKey(t.FilePath)) && !t.ExplicitlyAddedToLibrary &&
                (!_trackCatalog.TryGetValue(LibraryTrackKey(t.FilePath), out var known) || !known.ExplicitlyAddedToLibrary)).ToArray();
    }

    partial void OnPlaylistPendingDeletionChanged(Playlist? value)
    {
        KeepPlaylistSongsInLibrary = false;
        PlaylistDeletionError = null;
    }

    private bool CanConfirmDeletePlaylist() => _canEditPlaylists && !IsImportingPlaylist && !_isRelinkingTrack && PlaylistPendingDeletion is { } playlist &&
        Playlists.Contains(playlist);

    [RelayCommand(CanExecute = nameof(CanConfirmDeletePlaylist))]
    private void ConfirmDeletePlaylist()
    {
        if (!CanConfirmDeletePlaylist())
        {
            CancelDeletePlaylist();
            return;
        }
        var playlist = PlaylistPendingDeletion!;
        var retainedSongs = KeepPlaylistSongsInLibrary ? GetPlaylistExclusiveSongs() : [];
        try
        {
            // Do not delete based on stale membership after an earlier failed direct import.
            SaveLibrary();
            if (_librarySavePending) throw new InvalidOperationException("Library changes could not be saved. Try again before deleting this playlist.");
            if (_playlistStore is ILibraryMembershipStore membership)
                membership.DeletePlaylist(playlist.Id, KeepPlaylistSongsInLibrary);
            else
                _playlistStore?.Save(Playlists.Where(p => p != playlist).ToArray());
        }
        catch (Exception ex)
        {
            PlaylistDeletionError = $"Could not delete playlist: {ex.Message}";
            return;
        }
        if (retainedSongs.Length > 0)
            MarkTracksExplicit(retainedSongs, savePending: _playlistStore is not ILibraryMembershipStore);
        Playlists.Remove(playlist);
        if (SelectedPlaylist == playlist)
        {
            SelectedPlaylist = Playlists.FirstOrDefault();
            ClosePlaylist();
        }
        PlaylistPendingDeletion = null;
        SaveLibrary();
    }

    [RelayCommand]
    private void CancelDeletePlaylist() => PlaylistPendingDeletion = null;
}
