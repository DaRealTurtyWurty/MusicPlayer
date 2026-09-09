using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckGalleryPerformanceAsync()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        for (var i = 0; i < 1000; i++)
            vm.Tracks.Add(new Track { FilePath = $"gallery-{i}.wav", Title = $"Track {i}", Artist = $"Artist {i:D4}", Album = $"Album {i:D4}" });
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var view = new MusicBrowserView { DataContext = vm };
        void Layout(double width = 900)
        {
            view.Measure(new Size(width, 600));
            view.Arrange(new Rect(0, 0, width, 600));
            view.UpdateLayout();
        }
        foreach (var page in new[] { AppPage.Albums, AppPage.Artists, AppPage.Albums })
        {
            var stopwatch = Stopwatch.StartNew();
            vm.SelectedPage = page;
            Layout();
            var tiles = (ItemsControl)view.FindName("MusicTiles");
            var covers = Descendants(tiles).OfType<PlaylistCover>().Count();
            Console.WriteLine($"GALLERY: {page}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; {covers} realized covers / {tiles.Items.Count} items");
            Check(covers is > 0 and < 40 && tiles.Items.Count == 1000,
                $"{page} realizes only viewport cards for a large library");
        }
        var musicTiles = (ItemsControl)view.FindName("MusicTiles");
        var panel = Descendants(musicTiles).OfType<VirtualizingTilePanel>().Single();
        Check(panel.ScrollOwner is not null && panel.ExtentHeight > panel.ViewportHeight,
            "Gallery scrolling is delegated to the virtualizing panel");
        panel.SetVerticalOffset(double.PositiveInfinity);
        Layout();
        Check(Descendants(musicTiles).OfType<Button>().Any(b => ReferenceEquals(b.CommandParameter, vm.Albums[^1])),
            "Scrolling to the bottom realizes the final album");
        Check(Descendants(musicTiles).OfType<PlaylistCover>().Count() < 40,
            "Scrolling discards offscreen cards instead of accumulating them");
        Layout(420);
        Check(Descendants(musicTiles).OfType<PlaylistCover>().Count() < 20,
            "Narrow gallery resize adjusts the realized column count");
        Layout(1100);
        Check(Descendants(musicTiles).OfType<PlaylistCover>().Count() < 50,
            "Wide gallery resize keeps realization bounded");
        vm.MusicSearchText = "Album 0000";
        Layout();
        Check(musicTiles.Items.Count == 1 && panel.VerticalOffset == 0 &&
              Descendants(musicTiles).OfType<Button>().Any(b => ReferenceEquals(b.CommandParameter, vm.Albums[0])),
            "Filtering from the bottom resets scroll and displays the matching card");
        Check(ReferenceEquals(vm.VisibleMusicGroups, vm.VisibleMusicGroups),
            "Repeated gallery bindings reuse the filtered collection");
        vm.OpenMusicGroupCommand.Execute(vm.Albums[0]);
        vm.MusicSearchText = "Album";
        var galleryNotifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.VisibleMusicGroups)) galleryNotifications++; };
        vm.SelectedPage = AppPage.Artists;
        Check(galleryNotifications == 1, "Page navigation batches the detail/search reset into one gallery notification");
        view.DataContext = null;

        for (var i = 0; i < 1000; i++) vm.Playlists.Add(new Playlist { Name = $"Playlist {i}" });
        var playlistsView = new PlaylistsView { DataContext = vm };
        void LayoutPlaylists()
        {
            playlistsView.Measure(new Size(900, 600));
            playlistsView.Arrange(new Rect(0, 0, 900, 600));
            playlistsView.UpdateLayout();
        }
        LayoutPlaylists();
        var playlistTiles = (ItemsControl)playlistsView.FindName("PlaylistTiles");
        var playlistPanel = Descendants(playlistTiles).OfType<VirtualizingTilePanel>().Single();
        Check(Descendants(playlistTiles).OfType<PlaylistCover>().Count() is > 0 and < 40,
            "Playlist galleries also realize only viewport cards");
        playlistPanel.SetVerticalOffset(double.PositiveInfinity);
        LayoutPlaylists();
        Check(Descendants(playlistTiles).OfType<Button>().Any(b => ReferenceEquals(b.CommandParameter, vm.Playlists[^1])),
            "The final playlist remains reachable");
        vm.Playlists.RemoveAt(vm.Playlists.Count - 1);
        vm.Playlists.Move(0, vm.Playlists.Count - 1);
        LayoutPlaylists();
        Check(Descendants(playlistTiles).OfType<Button>().Any(b => ReferenceEquals(b.CommandParameter, vm.Playlists[^1])),
            "Removing and moving playlists updates realized cards and scroll bounds");
        vm.Playlists.Clear();
        LayoutPlaylists();
        Check(playlistPanel.ExtentHeight == 0 && playlistPanel.VerticalOffset == 0 &&
              !Descendants(playlistTiles).OfType<PlaylistCover>().Any(),
            "Clearing a scrolled gallery removes all cards and resets its extent");
        vm.Playlists.Add(new Playlist { Name = "New playlist" });
        LayoutPlaylists();
        Check(Descendants(playlistTiles).OfType<PlaylistCover>().Count() == 1,
            "Adding to an empty gallery realizes the new card");
        playlistsView.DataContext = null;

        var cover = new PlaylistCover { Tracks = [new Track { FilePath = "cover.wav", Title = "Cover" }] };
        var initialLoad = cover.ArtworkReady;
        cover.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(ReferenceEquals(initialLoad, cover.ArtworkReady), "Loaded does not restart the binding's artwork request");
        cover.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        await initialLoad;
        cover.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(!ReferenceEquals(initialLoad, cover.ArtworkReady), "A cancelled artwork request restarts when a card reloads");
        await cover.ArtworkReady;
    }
}
