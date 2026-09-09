using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckDeletePlaylistConfirmation()
    {
        var path = Path.Combine(Path.GetTempPath(), "MusicPlayerPlaylistConfirmation", Guid.NewGuid().ToString("N"), "music.db");
        var store = new SqliteMusicStore(path);
        var a = Track("A"); var b = Track("B");
        store.SavePlaylists([new Playlist([a, b]) { Name = "Evening listening" }, new Playlist([b]) { Name = "Keep this playlist" }]);
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            playlistStore: store, libraryStore: store);
        var first = vm.Playlists[0];
        var second = vm.Playlists[1];
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.SelectedTrack = b;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(a);
        vm.SelectedPage = AppPage.Playlists;
        vm.OpenPlaylistCommand.Execute(first);
        var playback = vm.QueueTimeline.ToArray();
        var window = new MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        vm.DeletePlaylistCommand.Execute(null);
        SaveMaintenancePreview(content, "delete-playlist-confirmation.png", 884, 561);
        var dialog = (FrameworkElement)window.FindName("DeletePlaylistDialog");
        var main = (FrameworkElement)window.FindName("MainContent");
        Check(vm.IsDeletePlaylistConfirmationOpen && vm.PlaylistPendingDeletion == first && vm.Playlists.Count == 2 &&
              store.LoadPlaylists().Count == 2 && dialog.Visibility == Visibility.Visible && !main.IsEnabled,
            "Deleting a playlist opens a modal without removing or saving anything yet");
        Check(((Button)window.FindName("CancelDeletePlaylistButton")).IsDefault &&
              dialog.InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Escape && b.Command == vm.CancelDeletePlaylistCommand),
            "Playlist deletion defaults to Cancel and supports Escape");
        vm.CancelDeletePlaylistCommand.Execute(null);
        Check(!vm.IsDeletePlaylistConfirmationOpen && vm.Playlists.Count == 2 && store.LoadPlaylists().Count == 2 &&
              vm.SelectedPlaylist == first && main.IsEnabled && vm.QueueTimeline.SequenceEqual(playback),
            "Cancelling playlist deletion preserves selection, saved playlists and playback");
        vm.ConfirmDeletePlaylistCommand.Execute(null);
        Check(vm.Playlists.Count == 2, "Playlist confirmation cannot delete anything when the modal is closed");
        vm.DeletePlaylistCommand.Execute(null);
        vm.SelectedPlaylist = second;
        vm.ConfirmDeletePlaylistCommand.Execute(null);
        Check(!vm.IsDeletePlaylistConfirmationOpen && vm.Playlists.Single() == second &&
              vm.SelectedPlaylist == second && store.LoadPlaylists().Single().Id == second.Id,
            "Confirmation deletes the named playlist even if selection changes before confirming");
        Check(vm.QueueTimeline.SequenceEqual(playback) && vm.IsPlaying && player.PlayCount == 2 && store.LoadLibrary().Count == 1,
            "Deleting a playlist hides exclusive songs but retains shared music, current playback, queue and history");
        vm.DeletePlaylistCommand.Execute(null);
        vm.ConfirmDeletePlaylistCommand.Execute(null);
        Check(vm.SelectedPlaylist is null && !vm.IsPlaylistOpen && store.LoadPlaylists().Count == 0,
            "Confirmed deletion of the last playlist returns to the gallery");
        vm.DeletePlaylistCommand.Execute(null);
        Check(!vm.IsDeletePlaylistConfirmationOpen, "No playlist selection cannot open a deletion modal");
        window.DataContext = null;
        window.Close();
    }
}
