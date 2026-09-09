using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicPlayer;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckMusicBrowserAsync()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        static async Task Refresh() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Track Song(string path, string? artist, string? album, string title) => new()
        {
            FilePath = path, Artist = artist, Album = album, Title = title, Duration = TimeSpan.FromMinutes(4),
            ArtworkData = CreateBrowserArtwork(path)
        };
        vm.SelectedPage = AppPage.Albums;
        Check(vm.Albums.Count == 0 && vm.Artists.Count == 0 && !vm.PlayMusicGroupCommand.CanExecute(null),
            "Empty album and artist browsers disable group playback");
        var a = Song("dive-1", "Tycho", "Dive", "A Walk");
        var b = Song("dive-2", " tycho ", " dive ", "Hours");
        var c = Song("awake", "Tycho", "Awake", "Awake");
        var other = Song("other", "Other artist", "Dive", "Different song");
        var unknown = Song("unknown", null, " ", "Untagged song");
        foreach (var track in new[] { a, b, c, other, unknown }) vm.Tracks.Add(track);
        await Refresh();
        Check(vm.Albums.Count == 4 && vm.Artists.Count == 3, "Groups normalize case/whitespace and separate same-named albums by artist");
        Check(vm.Albums.Any(g => g.Name == "Unknown album") && vm.Artists.Any(g => g.Name == "Unknown artist"),
            "Missing tags remain browsable under Unknown album and Unknown artist");
        vm.LibrarySearchText = "no matching songs";
        vm.ShowUncategorizedTracks = true;
        Check(vm.VisibleMusicGroups.Count == 4, "Album browsing is independent of the track library's search and filters");
        vm.MusicSearchText = "TYCHO";
        Check(vm.VisibleMusicGroups.Count == 2, "Album search matches tagged artists without case sensitivity");
        vm.MusicSearchText = "not found";
        Check(vm.VisibleMusicGroups.Count == 0 && vm.MusicEmptyMessage.Contains("No matches"), "Album search exposes an empty-results state");
        vm.ClearMusicSearchCommand.Execute(null);
        var dive = vm.Albums.Single(g => g.Name == "Dive" && g.Artist == "Tycho");
        vm.OpenMusicGroupCommand.Execute(dive);
        Check(vm.ShowBrowseTracks && vm.BrowseTracks.SequenceEqual(new[] { a, b }) && vm.BrowseSelection?.DurationSummary == "2 tracks · 8 min",
            "Album detail shows its tracks in stable order with total duration");
        vm.Queue.Add(other);
        vm.QueueMusicGroupCommand.Execute(null);
        Check(vm.Queue.SequenceEqual(new[] { other, a, b }) && !vm.IsPlaying, "Queue album appends tracks without starting playback");
        vm.PlayMusicGroupCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.Single() == b, "Play album starts a new playback session with its remaining tracks queued");
        vm.SelectedBrowseTrack = b;
        vm.PlayBrowseTrackNextCommand.Execute(null);
        Check(vm.Queue.Count == 2 && vm.Queue[0] == b, "Album track Play next preserves duplicate queue entries");
        vm.PlayBrowseTrackCommand.Execute(null);
        Check(vm.CurrentTrack == b, "Album track playback reuses the player's load behavior");
        vm.CloseMusicGroupCommand.Execute(null);
        Check(!vm.HasBrowseSelection && !vm.PlayBrowseTrackCommand.CanExecute(null), "Album back clears stale track selection");
        vm.SelectedPage = AppPage.Artists;
        var tycho = vm.Artists.Single(g => g.Name == "Tycho");
        vm.OpenMusicGroupCommand.Execute(tycho);
        Check(vm.IsArtistDetail && !vm.ShowBrowseTracks && vm.VisibleMusicGroups.Count == 2,
            "Artist detail opens a gallery of that artist's albums");
        vm.ToggleArtistTracksCommand.Execute(null);
        Check(vm.ShowBrowseTracks && vm.BrowseTracks.Count == 3, "Artist detail can show all of its tracks");
        vm.Queue.Clear();
        vm.QueueMusicGroupCommand.Execute(null);
        Check(vm.Queue.Count == 3 && vm.CurrentTrack == b, "Queue artist appends its full catalog without changing current playback");
        vm.ToggleArtistTracksCommand.Execute(null);
        vm.OpenMusicGroupCommand.Execute(dive);
        Check(vm.SelectedPage == AppPage.Artists && vm.MusicBackLabel == "Back to Tycho", "Artist-to-album navigation retains its parent");
        vm.SelectedBrowseTrack = b;
        var replacement = Song("dive-2", "Tycho", "Dive", "Updated title");
        vm.Tracks[vm.Tracks.IndexOf(b)] = replacement;
        await Refresh();
        Check(vm.SelectedAlbum?.Tracks.Contains(replacement) == true && vm.SelectedBrowseTrack == replacement,
            "Library metadata refresh updates open album details and preserves selection by path");
        vm.CloseMusicGroupCommand.Execute(null);
        Check(vm.IsArtistDetail && vm.VisibleMusicGroups.Count == 2, "Back from an artist's album restores its artist gallery");
        vm.Tracks.Remove(other);
        await Refresh();
        Check(vm.Artists.Count == 2 && vm.Albums.Count == 3, "Removing library membership removes empty albums and artists");

        for (var i = 0; i < 7; i++) vm.Tracks.Add(Song($"cover-{i}", $"Artist {i + 1}", $"Album {i + 1}", $"Track {i + 1}"));
        await Refresh();
        var window = new MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        content.SetValue(Panel.BackgroundProperty, window.Background);
        async Task Capture(string name, int width = 1180, int height = 680)
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            await Task.WhenAll(Descendants(content).OfType<PlaylistCover>().Select(cover => cover.ArtworkReady));
            await Refresh();
            // Offscreen windows can relayout at their default size while artwork awaits the dispatcher.
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var view = Descendants(content).OfType<MusicBrowserView>().Single();
            Check(view.ActualWidth > 0 && view.ActualHeight > 0, $"{name} renders through the main navigation");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            SaveTaskbarImage(bitmap, name + ".png");
        }
        vm.SelectedPage = AppPage.Albums;
        await Capture("albums-gallery");
        var view = Descendants(content).OfType<MusicBrowserView>().Single();
        var tiles = (ItemsControl)view.FindName("MusicTiles");
        Check(tiles.Items.Count == vm.Albums.Count, "Album gallery binds every visible album");
        var tile = Descendants(tiles).OfType<Button>().First();
        tile.Command.Execute(tile.CommandParameter);
        await Capture("album-detail");
        var list = (ListView)view.FindName("MusicTrackList");
        Check(list.Items.Count == vm.BrowseTracks.Count, "Album detail list binds its group's tracks");
        list.SelectedIndex = 0;
        Check(vm.SelectedBrowseTrack == vm.BrowseTracks[0], "Album track selection binds to playback actions");
        vm.SelectedPage = AppPage.Artists;
        await Capture("artists-gallery");
        vm.OpenMusicGroupCommand.Execute(vm.Artists.Single(g => g.Name == "Tycho"));
        await Capture("artist-albums");
        vm.ToggleArtistTracksCommand.Execute(null);
        vm.IsQueueOpen = true;
        await Capture("artist-tracks-compact", 884, 561);
        vm.Tracks.Clear();
        await Refresh();
        Check(!vm.HasBrowseSelection && vm.Artists.Count == 0 && !vm.QueueMusicGroupCommand.CanExecute(null),
            "Removing the selected artist returns to the gallery and disables stale commands");
        window.DataContext = null;
        window.Close();

        var preferencePath = Path.Combine(Path.GetTempPath(), "MusicBrowserPreferences", Guid.NewGuid().ToString("N"), "preferences.json");
        var preferences = new JsonUiPreferencesStore(preferencePath);
        foreach (var page in new[] { AppPage.Albums, AppPage.Artists })
        {
            preferences.SaveSelectedPage(page);
            Check(new JsonUiPreferencesStore(preferencePath).LoadSelectedPage() == page, $"{page} navigation survives restart");
        }
        Check((int)AppPage.Library == 0 && (int)AppPage.Playlists == 1 && (int)AppPage.NowPlaying == 2,
            "New pages preserve existing persisted navigation values");
    }

    private static byte[] CreateBrowserArtwork(string seed)
    {
        var hue = (uint)StringComparer.Ordinal.GetHashCode(seed);
        var color = Color.FromRgb((byte)(70 + hue % 150), (byte)(70 + (hue >> 8) % 150), (byte)(70 + (hue >> 16) % 150));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(23, 37, 50), color, 90), null, new Rect(0, 0, 150, 150));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, 235, 208, 167)), null, new Point(75, 68), 34, 34);
        }
        var bitmap = new RenderTargetBitmap(150, 150, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
