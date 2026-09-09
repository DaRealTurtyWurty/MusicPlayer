using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private static bool HasActionTrack(Track? track) => track is not null;

    [RelayCommand(CanExecute = nameof(HasActionTrack))]
    private void EnqueueTrack(Track? track)
    {
        if (track is not null) Queue.Add(track);
    }

    [RelayCommand(CanExecute = nameof(HasActionTrack))]
    private void PlayTrackNext(Track? track)
    {
        if (track is not null) Queue.Insert(0, track);
    }

    private MusicGroup AlbumForTrack(Track track)
    {
        var key = $"{MusicGroup.Normalize(MusicGroup.ArtistName(track))}\0{MusicGroup.Normalize(MusicGroup.AlbumName(track))}";
        // Restored queue entries can refer to music that is no longer in the library.
        return Albums.FirstOrDefault(g => g.Key == key) ?? ClassifyRelease(new MusicGroup(
            MusicGroupKind.Album, MusicGroup.AlbumName(track), MusicGroup.ArtistName(track), [track], []));
    }

    private void PrepareTrackNavigation(AppPage page)
    {
        // Consume queued library updates now so they cannot immediately clear the newly opened detail.
        _musicBrowserRefresh?.Abort();
        _musicBrowserRefresh = null;
        RefreshMusicGroups();
        SelectedPage = page;
        ResetMusicBrowser();
    }

    [RelayCommand(CanExecute = nameof(HasActionTrack))]
    private void ShowAlbum(Track? track)
    {
        if (track is null) return;
        PrepareTrackNavigation(AppPage.Albums);
        var album = AlbumForTrack(track);
        OpenMusicGroup(album);
        SelectedBrowseTrack = album.Tracks.FirstOrDefault(t =>
            string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand(CanExecute = nameof(HasActionTrack))]
    private void ShowArtist(Track? track)
    {
        if (track is null) return;
        PrepareTrackNavigation(AppPage.Artists);
        var name = MusicGroup.ArtistName(track);
        var artist = Artists.FirstOrDefault(g => g.Key == MusicGroup.Normalize(name)) ??
            new MusicGroup(MusicGroupKind.Artist, name, name, [track], [AlbumForTrack(track)]);
        OpenMusicGroup(artist);
    }

    [RelayCommand(CanExecute = nameof(HasActionTrack))]
    private void OpenFileLocation(Track? track)
    {
        if (track is null) return;
        try
        {
            _fileLocationService.ShowFile(track.FilePath);
            PlaybackError = null;
        }
        catch (Exception ex)
        {
            PlaybackError = $"Could not open the file location for {track.Title}: {ex.Message}";
        }
    }
}
