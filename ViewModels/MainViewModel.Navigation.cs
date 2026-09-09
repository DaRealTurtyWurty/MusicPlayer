using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private IPlaylistStore? _playlistStore;
    private bool _canEditPlaylists = true;

    [ObservableProperty] private AppPage selectedPage = AppPage.Library;

    partial void OnSelectedPageChanged(AppPage value)
    {
        _uiPreferencesStore?.SaveSelectedPage(value);
        ResetMusicBrowser();
    }

    [ObservableProperty] private bool isPlaylistOpen;

    [ObservableProperty] private bool isRenamingPlaylist;

    [ObservableProperty] private Playlist? selectedPlaylist;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RenamePlaylistCommand))]
    private string playlistName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPlaylistTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemovePlaylistTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePlaylistTrackUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePlaylistTrackDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(QueuePlaylistTrackCommand))]
    private int selectedPlaylistTrackIndex = -1;

    [ObservableProperty] private string? playlistError;

    [ObservableProperty] private string? playlistMessage;

    public ObservableCollection<Playlist> Playlists { get; } = [];

    private void InitializePages(IPlaylistStore? playlistStore)
    {
        _playlistStore = playlistStore;
        try
        {
            foreach (var playlist in _playlistStore?.Load() ?? [])
                Playlists.Add(playlist);
            SelectedPlaylist = Playlists.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Do not overwrite storage we could not read.
            _canEditPlaylists = false;
            PlaylistError = $"Could not load saved playlists: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Navigate(AppPage page) => SelectedPage = page;

    [RelayCommand]
    private void OpenPlaylist(Playlist playlist)
    {
        SelectedPlaylist = playlist;
        IsPlaylistOpen = true;
        IsRenamingPlaylist = false;
    }

    [RelayCommand]
    private void ClosePlaylist()
    {
        IsPlaylistOpen = false;
        IsRenamingPlaylist = false;
    }

    [RelayCommand(CanExecute = nameof(HasPlaylist))]
    private void BeginRenamePlaylist()
    {
        PlaylistName = SelectedPlaylist!.Name;
        IsRenamingPlaylist = true;
    }

    [RelayCommand]
    private void CancelRenamePlaylist()
    {
        PlaylistName = SelectedPlaylist?.Name ?? "";
        IsRenamingPlaylist = false;
    }

    private bool CanEditPlaylists() => _canEditPlaylists;
    private bool HasPlaylist() => _canEditPlaylists && SelectedPlaylist is not null;
    private bool HasPlaylistTracks() => SelectedPlaylist?.Tracks.Count > 0;
    private bool CanRenamePlaylist() => HasPlaylist() && !string.IsNullOrWhiteSpace(PlaylistName);
    private bool CanAddToPlaylist() => HasPlaylist() && SelectedTrack is not null;

    private bool HasPlaylistTrackSelection() => SelectedPlaylist is not null &&
                                                SelectedPlaylistTrackIndex >= 0 && SelectedPlaylistTrackIndex <
                                                SelectedPlaylist.Tracks.Count;

    private bool CanEditPlaylistTrack() => HasPlaylist() && HasPlaylistTrackSelection();
    private bool CanMovePlaylistTrackUp() => CanEditPlaylistTrack() && SelectedPlaylistTrackIndex > 0;

    private bool CanMovePlaylistTrackDown() =>
        CanEditPlaylistTrack() && SelectedPlaylistTrackIndex < SelectedPlaylist!.Tracks.Count - 1;

    [RelayCommand(CanExecute = nameof(CanEditPlaylists))]
    private void CreatePlaylist()
    {
        var number = 1;
        while (Playlists.Any(p => p.Name == $"Playlist {number}")) number++;
        var playlist = new Playlist { Name = $"Playlist {number}" };
        Playlists.Add(playlist);
        SelectedPlaylist = playlist;
        SelectedPage = AppPage.Playlists;
        IsPlaylistOpen = true;
        IsRenamingPlaylist = true;
        SavePlaylists();
    }

    [RelayCommand(CanExecute = nameof(CanRenamePlaylist))]
    private void RenamePlaylist()
    {
        SelectedPlaylist!.Name = PlaylistName.Trim();
        PlaylistName = SelectedPlaylist.Name;
        IsRenamingPlaylist = false;
        SavePlaylists();
    }

    [RelayCommand(CanExecute = nameof(HasPlaylist))]
    private void DeletePlaylist()
    {
        if (HasPlaylist() && !IsDeletePlaylistConfirmationOpen)
            PlaylistPendingDeletion = SelectedPlaylist;
    }

    [RelayCommand(CanExecute = nameof(CanAddToPlaylist))]
    private void AddSelectedTrackToPlaylist()
    {
        SelectedPlaylist!.Tracks.Add(SelectedTrack!);
        SavePlaylists();
        if (PlaylistError is null)
            PlaylistMessage = $"Added {SelectedTrack!.Title} to {SelectedPlaylist.Name}.";
    }

    [RelayCommand(CanExecute = nameof(HasPlaylistTracks))]
    private void PlayPlaylist() => StartPlaybackSession(SelectedPlaylist!.Tracks);

    [RelayCommand(CanExecute = nameof(HasPlaylistTracks))]
    private void QueuePlaylist()
    {
        foreach (var track in SelectedPlaylist!.Tracks) Queue.Add(track);
    }

    [RelayCommand(CanExecute = nameof(HasPlaylistTrackSelection))]
    private void PlayPlaylistTrack() =>
        LoadTrack(SelectedPlaylist!.Tracks[SelectedPlaylistTrackIndex], playImmediately: true);

    [RelayCommand(CanExecute = nameof(HasPlaylistTrackSelection))]
    private void QueuePlaylistTrack() => Queue.Add(SelectedPlaylist!.Tracks[SelectedPlaylistTrackIndex]);

    [RelayCommand(CanExecute = nameof(CanEditPlaylistTrack))]
    private void RemovePlaylistTrack()
    {
        var index = SelectedPlaylistTrackIndex;
        SelectedPlaylist!.Tracks.RemoveAt(index);
        SelectedPlaylistTrackIndex = Math.Min(index, SelectedPlaylist.Tracks.Count - 1);
        SavePlaylists();
    }

    [RelayCommand(CanExecute = nameof(CanMovePlaylistTrackUp))]
    private void MovePlaylistTrackUp() => MovePlaylistTrack(-1);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistTrackDown))]
    private void MovePlaylistTrackDown() => MovePlaylistTrack(1);

    private void MovePlaylistTrack(int direction)
    {
        var index = SelectedPlaylistTrackIndex;
        SelectedPlaylist!.Tracks.Move(index, index + direction);
        SelectedPlaylistTrackIndex = index + direction;
        SavePlaylists();
    }

    partial void OnSelectedPlaylistChanging(Playlist? value)
    {
        if (SelectedPlaylist is not null)
            SelectedPlaylist.Tracks.CollectionChanged -= OnPlaylistTracksChanged;
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        PlaylistName = value?.Name ?? "";
        SelectedPlaylistTrackIndex = -1;
        PlaylistMessage = null;
        if (value is not null)
            value.Tracks.CollectionChanged += OnPlaylistTracksChanged;
        UpdatePlaylistCommands();
    }

    private void OnPlaylistTracksChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdatePlaylistCommands();

    partial void OnSelectedTrackChanged(Track? value)
    {
        AddSelectedTrackToPlaylistCommand.NotifyCanExecuteChanged();
        PlaylistMessage = null;
    }

    private void UpdatePlaylistCommands()
    {
        RenamePlaylistCommand.NotifyCanExecuteChanged();
        BeginRenamePlaylistCommand.NotifyCanExecuteChanged();
        DeletePlaylistCommand.NotifyCanExecuteChanged();
        AddSelectedTrackToPlaylistCommand.NotifyCanExecuteChanged();
        PlayPlaylistCommand.NotifyCanExecuteChanged();
        AddTracksToPlaylistCommand.NotifyCanExecuteChanged();
        QueuePlaylistCommand.NotifyCanExecuteChanged();
        PlayPlaylistTrackCommand.NotifyCanExecuteChanged();
        QueuePlaylistTrackCommand.NotifyCanExecuteChanged();
        RemovePlaylistTrackCommand.NotifyCanExecuteChanged();
        MovePlaylistTrackUpCommand.NotifyCanExecuteChanged();
        MovePlaylistTrackDownCommand.NotifyCanExecuteChanged();
    }

    private void SavePlaylists()
    {
        if (_isRelinkingTrack) { _playlistSaveDeferred = true; return; }
        try
        {
            _playlistStore?.Save(Playlists);
            PlaylistError = null;
            PlaylistMessage = null;
        }
        catch (Exception ex)
        {
            PlaylistError = $"Changes could not be saved: {ex.Message}";
        }
    }
}
