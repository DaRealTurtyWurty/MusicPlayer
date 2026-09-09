using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--lyrics-smoke"])
        {
            CheckLyricsAsync().GetAwaiter().GetResult();
            return;
        }
        if (args is ["--artist-cache-smoke"])
        {
            DispatcherTest.Run(CheckArtistCachePriorityAsync);
            return;
        }
        if (args is ["--artist-matching-live"])
        {
            DispatcherTest.Run(CheckArtistMatchingLiveAsync);
            return;
        }
        if (args is ["--artist-photo-smoke"] or ["--artist-photo-live"] or ["--artist-deezer-live"] or ["--artist-custom-smoke"] or ["--artist-deezer-search-smoke"])
        {
            var photoApp = new App();
            photoApp.InitializeComponent();
            photoApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            DispatcherTest.Run(args[0] switch
            {
                "--artist-photo-live" => CheckArtistPhotoLiveAsync,
                "--artist-deezer-live" => CheckDeezerLiveAsync,
                "--artist-deezer-search-smoke" => async () =>
                {
                    using var temporary = new TemporaryTestDirectory("MusicPlayerDeezerSearchTests");
                    await CheckDeezerSearchAsync(temporary.Path);
                },
                "--artist-custom-smoke" => async () =>
                {
                    using var temporary = new TemporaryTestDirectory("MusicPlayerCustomPhotoTests");
                    await CheckCustomArtistPhotosAsync(temporary.Path);
                },
                _ => CheckArtistPhotosAsync
            });
            return;
        }
        if (args is ["--artist-identity-live"])
        {
            DispatcherTest.Run(async () =>
            {
                var service = new MusicBrainzArtistService();
                foreach (var name in new[] { "Radiohead", "AC/DC" })
                {
                    var identity = await service.IdentifyAsync(name, [], default);
                    Check(identity.Status == ArtistIdentityStatus.Identified, $"Live MusicBrainz lookup: {name} -> {identity.MusicBrainzId} ({identity.Status})");
                }
            });
            return;
        }
        if (args is ["--artist-identity-smoke"])
        {
            DispatcherTest.Run(CheckArtistIdentityAsync);
            return;
        }
        if (args is ["--gallery-performance"])
        {
            var galleryApp = new App();
            galleryApp.InitializeComponent();
            galleryApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            DispatcherTest.Run(CheckGalleryPerformanceAsync);
            return;
        }
        if (args is ["--temp-cleanup-smoke"])
        {
            TemporaryTestDirectoryTests.Run();
            DispatcherTest.Run(PlaylistImportTests.RunAsync);
            var artworkApp = new App();
            artworkApp.InitializeComponent();
            artworkApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            CheckRestoredPlaylistArtwork();
            Console.WriteLine("Temporary directory cleanup tests passed.");
            return;
        }
        if (args is ["--release-type-smoke"])
        {
            var releaseApp = new App();
            releaseApp.InitializeComponent();
            releaseApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            DispatcherTest.Run(CheckReleaseTypesAsync);
            return;
        }
        if (args is ["--music-browser-smoke"])
        {
            var browserApp = new App();
            browserApp.InitializeComponent();
            browserApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            DispatcherTest.Run(CheckMusicBrowserAsync);
            return;
        }
        if (args is ["--taskbar-smoke"])
        {
            var taskbarApp = new App();
            taskbarApp.InitializeComponent();
            taskbarApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            DispatcherTest.Run(CheckTaskbarPreviewAsync);
            return;
        }
        if (args is ["--system-media-smoke"])
        {
            DispatcherTest.Run(CheckSystemMediaControlsAsync);
            CheckNativeSystemMediaControls();
            DispatcherTest.Run(CheckSystemMediaSessionAsync);
            return;
        }
        if (args is ["--create-fixtures", var directory])
        {
            FixtureLibrary.Write(directory);
            return;
        }
        TemporaryTestDirectoryTests.Run();
        CheckLyricsAsync().GetAwaiter().GetResult();
        var player = new FakePlayer();
        DispatcherTest.Run(CheckArtistIdentityAsync);
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var a = Track("A");
        var b = Track("B");
        var c = Track("C");

        Check(!vm.NextCommand.CanExecute(null) && !vm.AddToQueueCommand.CanExecute(null), "Empty queue commands");
        vm.SelectedTrack = a;
        vm.AddToQueueCommand.Execute(null);
        vm.AddToQueueCommand.Execute(null);
        vm.SelectedTrack = b;
        vm.PlayNextCommand.Execute(null);
        Check(vm.Queue.SequenceEqual(new[] { b, a, a }), "Play next and duplicates");
        vm.PlayCommand.Execute(null);
        Check(vm.CurrentTrack == b && player.PlayCount == 1 && vm.Queue.Count == 2, "Play starts a queued track");
        vm.SelectedQueueIndex = 1;
        vm.RemoveFromQueueCommand.Execute(null);
        Check(vm.Queue.Count == 1 && vm.Queue[0] == a, "Remove one duplicate");
        player.End();
        Check(vm.CurrentTrack == a && vm.Queue.Count == 0 && player.PlayCount == 2, "Natural completion advances");
        player.End();
        Check(player.PlayCount == 2 && !vm.NextCommand.CanExecute(null), "Queue exhaustion stops");
        vm.PlayCommand.Execute(null);
        Check(player.Position == TimeSpan.Zero && player.PlayCount == 3, "Replay after completion");
        vm.TogglePlaybackCommand.Execute(null);
        Check(!vm.IsPlaying && vm.PlayPauseLabel == "Play", "Toggle pauses and updates UI");
        vm.TogglePlaybackCommand.Execute(null);
        Check(vm.IsPlaying && vm.PlayPauseLabel == "Pause", "Toggle resumes and updates UI");

        vm.Tracks.Add(a);
        vm.Tracks.Add(b);
        vm.Tracks.Add(c);
        vm.PlayAllCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, c }), "Play all follows library order");
        vm.SelectedQueueIndex = 1;
        vm.MoveQueueUpCommand.Execute(null);
        Check(vm.Queue.SequenceEqual(new[] { c, b }) && vm.SelectedQueueIndex == 0, "Move up");
        Check(!vm.MoveQueueUpCommand.CanExecute(null), "Upper reorder boundary");
        vm.MoveQueueDownCommand.Execute(null);
        Check(vm.Queue.SequenceEqual(new[] { b, c }) && vm.SelectedQueueIndex == 1, "Move down");
        Check(!vm.MoveQueueDownCommand.CanExecute(null), "Lower reorder boundary");
        vm.PlayQueuedTrackCommand.Execute(null);
        Check(vm.CurrentTrack == c && vm.Queue.Single() == b, "Play selected queue entry");
        vm.PauseCommand.Execute(null);
        Check(vm.Queue.Count == 1, "Pause preserves queue");
        vm.StopCommand.Execute(null);
        Check(vm.Queue.Count == 1 && vm.PositionSeconds == 0, "Stop preserves queue");
        vm.ClearQueueCommand.Execute(null);
        vm.ConfirmClearQueueCommand.Execute(null);
        Check(vm.CurrentTrack == c && vm.IsQueueEmpty, "Clear preserves current song");

        vm.SelectedTrack = Track("broken");
        vm.AddToQueueCommand.Execute(null);
        vm.SelectedTrack = a;
        vm.AddToQueueCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.IsQueueEmpty, "Skip unreadable queued tracks");
        vm.SelectedTrack = Track("broken");
        vm.PlaySelectedTrackCommand.Execute(null);
        Check(vm.CurrentTrack is null && vm.PlaybackError is not null, "Report load failures");

        CheckShuffleAndRepeat();
        CheckPrevious();
        CheckVolumePersistence();
        CheckPlaybackModePersistence();
        DispatcherTest.Run(CheckSystemMediaControlsAsync);
        CheckNativeSystemMediaControls();
        DispatcherTest.Run(CheckPlaybackSessionPersistenceAsync);
        SqlitePersistenceTests.Run();
        CheckPlaylistsAndNavigation();
        DispatcherTest.Run(PlaylistImportTests.RunAsync);
        DispatcherTest.Run(LibraryTests.RunAsync);
        DispatcherTest.Run(LibraryRefreshTests.RunAsync);

        // Load the compiled XAML and exercise its bindings/layout without audio hardware.
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        DispatcherTest.Run(CheckArtistPhotosAsync);
        DispatcherTest.Run(CheckReleaseTypesAsync);
        DispatcherTest.Run(CheckMusicBrowserAsync);
        DispatcherTest.Run(CheckGalleryPerformanceAsync);
        DispatcherTest.Run(CheckTaskbarPreviewAsync);
        DispatcherTest.Run(CheckLibraryMaintenanceAsync);
        DispatcherTest.Run(CheckLibraryMembershipAsync);
        CheckVolume(args.Length > 0 ? args[0] + ".volume.png" : null);
        CheckQueueVisibility();
        CheckQueueHistory();
        CheckClearQueueConfirmation();
        CheckDeletePlaylistConfirmation();
        CheckQueueInteractions();
        CheckTrackMenus();
        CheckTrackActions();
        CheckLargePlaylistLayout();
        CheckRestoredPlaylistArtwork();
        var window = new MainWindow(vm);
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(b);
        vm.Queue.Add(c);
        window.DataContext = vm;
        window.Measure(new System.Windows.Size(1180, 720));
        window.Arrange(new System.Windows.Rect(0, 0, 1180, 720));
        window.UpdateLayout();
        if (args.Length > 0)
        {
            vm.Tracks.Clear();
            vm.Queue.Clear();
            var titles = new[] { "Weightless", "A Walk", "First Breath After Coma", "Awake", "Open Eye Signal", "Dayvan Cowboy", "Says", "An Ending (Ascent)" };
            var artists = new[] { "Marconi Union", "Tycho", "Explosions in the Sky", "Tycho", "Jon Hopkins", "Boards of Canada", "Nils Frahm", "Brian Eno" };
            var albums = new[] { "Ambient Transmissions", "Dive", "The Earth Is Not a Cold Dead Place", "Awake", "Immunity", "The Campfire Headphase", "Spaces", "Apollo" };
            for (var i = 0; i < titles.Length; i++)
                vm.Tracks.Add(new Track { FilePath = titles[i], Title = titles[i], Artist = artists[i], Album = albums[i], Duration = TimeSpan.FromSeconds(210 + i * 17) });
            if (args.Length > 1)
            {
                // Optional read-only artwork source for local visual QA; playlists stay in memory.
                var metadata = new TagLibMetadataService();
                var artworkTracks = System.IO.Directory.EnumerateFiles(args[1], "*.flac", System.IO.SearchOption.AllDirectories)
                    .Select(metadata.ReadTrack).Where(t => t.ArtworkData is { Length: > 0 })
                    .DistinctBy(t => t.Album).Take(8).ToArray();
                if (artworkTracks.Length >= 4)
                {
                    vm.Tracks.Clear();
                    foreach (var track in artworkTracks) vm.Tracks.Add(track);
                }
            }
            vm.SelectedTrack = vm.Tracks[1];
            vm.PlaySelectedTrackCommand.Execute(null);
            foreach (var track in vm.Tracks.Skip(2).Take(4)) vm.Queue.Add(track);
            vm.SelectedQueueIndex = 1;
            vm.ToggleShuffleCommand.Execute(null);
            vm.CycleRepeatCommand.Execute(null);
            Render(window, args[0], 1280, 780);
            vm.CycleRepeatCommand.Execute(null);
            Render(window, args[0] + ".small.png", 884, 561);
            vm.IsQueueOpen = true;
            Render(window, args[0] + ".queue-open.small.png", 884, 561);
            vm.IsQueueOpen = false;
            vm.CreatePlaylistCommand.Execute(null);
            vm.PlaylistName = "Evening listening";
            vm.RenamePlaylistCommand.Execute(null);
            foreach (var track in vm.Tracks.Take(4))
            {
                vm.SelectedTrack = track;
                vm.AddSelectedTrackToPlaylistCommand.Execute(null);
            }
            Render(window, args[0] + ".playlists.png", 1280, 780);
            Render(window, args[0] + ".playlists.small.png", 884, 561);
            vm.ClosePlaylistCommand.Execute(null);
            var extraPlaylist = new Playlist { Name = "On repeat" };
            foreach (var track in vm.Tracks.Skip(3).Take(2)) extraPlaylist.Tracks.Add(track);
            vm.Playlists.Add(extraPlaylist);
            var singlePlaylist = new Playlist { Name = "Weekend" };
            singlePlaylist.Tracks.Add(vm.Tracks.Last());
            vm.Playlists.Add(singlePlaylist);
            Render(window, args[0] + ".playlist-gallery.png", 1280, 780);
            Render(window, args[0] + ".playlist-gallery.small.png", 884, 521);
            extraPlaylist.Tracks.Add(vm.Tracks[5]);
            Render(window, args[0] + ".three-covers.png", 1280, 780);
            vm.IsImportingPlaylist = true;
            vm.ImportProgress = new(42, 180, 2, "First Breath After Coma.flac");
            Render(window, args[0] + ".import-progress.png", 884, 521);
            vm.ImportProgress = new(180, null, 0, null);
            Render(window, args[0] + ".import-discovery.png", 1280, 780);
            vm.IsImportingPlaylist = false;
            vm.OpenPlaylistCommand.Execute(vm.SelectedPlaylist);
            vm.BeginRenamePlaylistCommand.Execute(null);
            Render(window, args[0] + ".playlist-rename.small.png", 884, 521);
            vm.CancelRenamePlaylistCommand.Execute(null);
            vm.NavigateCommand.Execute(AppPage.NowPlaying);
            Render(window, args[0] + ".nowplaying.png", 884, 561);
            vm.NavigateCommand.Execute(AppPage.Library);
            vm.CycleRepeatCommand.Execute(null);
            vm.ToggleShuffleCommand.Execute(null);
            vm.Tracks.Clear();
            vm.Queue.Clear();
            vm.CurrentTrack = null;
            vm.IsPlaying = false;
            Render(window, args[0] + ".empty.png", 884, 561);
            vm.IsQueueOpen = true;
            Render(window, args[0] + ".empty.queue-open.png", 884, 561);
        }
        window.Close();
        Console.WriteLine("All queue behavior and XAML smoke checks passed.");
    }

    private static async Task CheckPlaybackSessionPersistenceAsync()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MusicPlayerSessionTests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "music.db");
        var store = new SqliteMusicStore(path);
        MainViewModel Create(FakePlayer player) => new(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            playbackSessionStore: new SqliteMusicStore(path));
        var player = new FakePlayer();
        using (var vm = Create(player))
        {
            vm.SelectedTrack = Track("A");
            vm.PlaySelectedTrackCommand.Execute(null);
            vm.Queue.Add(Track("B"));
            vm.Queue.Add(Track("A"));
            vm.Queue.Add(Track("B"));
            player.Seek(TimeSpan.FromSeconds(42.75));
            await Task.Delay(2300);
            Check(store.LoadSession().Position.TotalSeconds == 42.75 && store.LoadSession().Queue.Count == 3,
                "Playback session is checkpointed while the app remains open");
            player.Seek(TimeSpan.FromSeconds(48.25));
        }
        player = new FakePlayer();
        using (var restored = Create(player))
        {
            Check(restored.CurrentTrack?.Title == "A" && restored.PositionSeconds == 48.25 && player.Position.TotalSeconds == 48.25 &&
                  !restored.IsPlaying && player.PlayCount == 0 && restored.DurationSeconds == 180,
                "Closing saves the latest backend position and restarting restores the current track paused at that position");
            Check(restored.Queue.Select(t => t.Title).SequenceEqual(new[] { "B", "A", "B" }),
                "Restart restores duplicate queue entries in their exact order");
            restored.PlayCommand.Execute(null);
            Check(player.PlayCount == 1 && player.Position.TotalSeconds == 48.25, "Play resumes the restored track without restarting it");
            restored.SelectedQueueIndex = 1;
            restored.MoveQueueUpCommand.Execute(null);
            restored.SelectedQueueIndex = 2;
            restored.RemoveFromQueueCommand.Execute(null);
            restored.NextCommand.Execute(null);
            restored.PositionSeconds = 15;
            restored.PauseCommand.Execute(null);
        }
        using (var edited = Create(new FakePlayer()))
        {
            Check(edited.QueueTimeline.Select(e => (e.Track.Title, e.Kind)).SequenceEqual(new[]
                {
                    ("A", QueueEntryKind.History), ("A", QueueEntryKind.Current), ("B", QueueEntryKind.Upcoming)
                }), "Restart restores played, current and upcoming occurrences, including duplicate tracks");
            Check(edited.CurrentTrack?.Title == "A" && edited.Queue.Single().Title == "B" && edited.PositionSeconds == 15,
                "Queue reorder, removal, track advance and seeking survive restart together");
            edited.ClearQueueCommand.Execute(null);
            edited.ConfirmClearQueueCommand.Execute(null);
            edited.StopCommand.Execute(null);
        }
        using (var stopped = Create(new FakePlayer()))
            Check(stopped.CurrentTrack?.Title == "A" && stopped.Queue.Count == 0 && stopped.PositionSeconds == 0,
                "Clearing the queue and stopping persist an empty queue and zero position");

        store.SaveSession(new PlaybackSession(Track("A"), TimeSpan.FromMinutes(10), new[] { Track("B") }));
        using (var shorter = Create(new FakePlayer()))
            Check(shorter.PositionSeconds == 180, "Restored playback position is clamped to the actual track duration");
        store.SaveSession(new PlaybackSession(Track("broken"), TimeSpan.FromSeconds(30), new[] { Track("B") }));
        using (var missing = Create(new FakePlayer()))
            Check(missing.PlaybackError is not null && missing.CurrentTrack is null && missing.Queue.Single().Title == "B" && !missing.IsPlaying,
                "An unavailable current track reports an error while retaining the restored queue");
        Check(store.LoadSession().CurrentTrack?.Title == "broken", "Closing after a failed track restore preserves the saved track for recovery");
        using (var recovery = Create(new FakePlayer()))
        {
            recovery.NextCommand.Execute(null);
            Check(recovery.CurrentTrack?.Title == "B" && recovery.IsPlaying, "The restored queue remains playable after a missing current track");
        }
    }

    private static void CheckVolumePersistence()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MusicPlayerVolumeTests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "preferences.json");
        MainViewModel Create(FakePlayer player) => new(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            uiPreferencesStore: new JsonUiPreferencesStore(path));
        var player = new FakePlayer();
        using (var vm = Create(player))
        {
            Check(vm.Volume == 100 && player.Volume == 1f && !vm.IsMuted, "Missing volume preferences default to full gain");
            vm.Volume = 37.5;
            vm.IsQueueOpen = true;
            vm.SelectedPage = AppPage.Playlists;
        }
        using (var restored = Create(player))
        {
            Check(restored.Volume == 37.5 && player.Volume == 0.375f && restored.IsQueueOpen && restored.SelectedPage == AppPage.Playlists,
                "Volume survives restart and preference changes preserve volume, navigation and queue visibility");
            restored.ToggleMuteCommand.Execute(null);
        }
        using (var muted = Create(player))
        {
            Check(muted.IsMuted && player.Volume == 0, "Restart restores mute before any track is loaded");
            muted.ToggleMuteCommand.Execute(null);
            Check(muted.Volume == 37.5 && player.Volume == 0.375f, "Unmute after restart restores the saved audible level");
            muted.Volume = 0;
        }
        using (var zero = Create(player))
        {
            zero.ToggleMuteCommand.Execute(null);
            Check(zero.Volume == 37.5, "Setting the slider to zero persists its previous audible level");
        }
        System.IO.File.WriteAllText(path, "{\"IsQueueOpen\":true}");
        using (var legacy = Create(player))
            Check(legacy.Volume == 100 && legacy.IsQueueOpen, "Older preferences without volume fields retain existing settings and default volume");
        System.IO.File.WriteAllText(path, "{\"Volume\":-10,\"VolumeBeforeMute\":0}");
        using (var invalid = Create(player))
        {
            Check(invalid.IsMuted && player.Volume == 0, "Restored negative volume is clamped to silence");
            invalid.ToggleMuteCommand.Execute(null);
            Check(invalid.Volume == 100, "Invalid saved unmute volume falls back to an audible default");
        }
        System.IO.File.WriteAllText(path, "{\"Volume\":250}");
        using (var excessive = Create(player))
            Check(excessive.Volume == 100 && player.Volume == 1f, "Restored volume cannot exceed full gain");
        System.IO.File.WriteAllText(path, "invalid JSON");
        using (var corrupt = Create(player))
            Check(corrupt.Volume == 100 && !corrupt.IsMuted, "Corrupt volume preferences use startup defaults");

        // A directory at the destination forces a save failure without changing playback behavior.
        var blockedPath = System.IO.Path.Combine(directory, "blocked");
        System.IO.Directory.CreateDirectory(blockedPath);
        using var unavailable = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            uiPreferencesStore: new JsonUiPreferencesStore(blockedPath));
        unavailable.Volume = 25;
        Check(unavailable.Volume == 25 && player.Volume == 0.25f, "Volume remains usable when preferences cannot be saved");
    }

    private static void CheckVolume(string? previewPath)
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var window = new MainWindow(vm)
        {
            ShowActivated = false, ShowInTaskbar = false,
            Width = 900, Height = 600,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -10000, Top = -10000
        };
        window.Show();
        var slider = (System.Windows.Controls.Slider)window.FindName("VolumeSlider");
        var mute = (System.Windows.Controls.Button)window.FindName("MuteButton");
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("VolumePopup");
        var popupContent = (System.Windows.FrameworkElement)window.FindName("VolumePopupContent");
        var content = (System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(884, 561));
        content.Arrange(new System.Windows.Rect(0, 0, 884, 561));
        content.UpdateLayout();
        Check(!popup.IsOpen, "Volume popup starts closed");
        mute.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = System.Windows.UIElement.MouseEnterEvent
        });
        popupContent.Measure(new System.Windows.Size(64, 200));
        popupContent.Arrange(new System.Windows.Rect(popupContent.DesiredSize));
        popupContent.UpdateLayout();
        Check(popup.IsOpen && slider.Orientation == System.Windows.Controls.Orientation.Vertical,
            "Hovering the volume icon opens a vertical slider");
        Check(slider.IsEnabled && slider.Value == 100 && player.Volume == 1f,
            "Volume is available before loading a track and starts at full gain");
        slider.Value = 35;
        Check(vm.Volume == 35 && Math.Abs(player.Volume - 0.35f) < 0.0001f,
            "Volume slider immediately updates playback gain");
        mute.Command.Execute(null);
        Check(vm.IsMuted && slider.Value == 0 && player.Volume == 0 && vm.MuteLabel == "Unmute",
            "Mute silences playback and updates the slider and accessible label");
        vm.Queue.Add(Track("A"));
        vm.Queue.Add(Track("B"));
        vm.PlayCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Check(player.Volume == 0 && vm.IsMuted, "Changing tracks preserves mute");
        mute.Command.Execute(null);
        Check(slider.Value == 35 && Math.Abs(player.Volume - 0.35f) < 0.0001f,
            "Unmute restores the previous listening level");
        vm.ToggleMuteCommand.Execute(null);
        slider.Value = 60;
        Check(!vm.IsMuted && Math.Abs(player.Volume - 0.6f) < 0.0001f,
            "Raising the slider while muted restores audible playback");
        vm.PauseCommand.Execute(null);
        slider.Value = 25;
        vm.PlayCommand.Execute(null);
        Check(Math.Abs(player.Volume - 0.25f) < 0.0001f, "Volume changes while paused survive resume");
        vm.Volume = -10;
        vm.ToggleMuteCommand.Execute(null);
        Check(vm.Volume == 25, "Zero volume retains the last audible level for unmute");
        vm.Volume = 200;
        vm.Volume = double.NaN;
        Check(vm.Volume == 100 && player.Volume == 1f, "Invalid volume cannot exceed full gain");
        slider.Value = 25;
        popupContent.UpdateLayout();
        var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
        var lowThumb = track.Thumb.TranslatePoint(new System.Windows.Point(), slider).Y;
        slider.Value = 75;
        popupContent.UpdateLayout();
        var highThumb = track.Thumb.TranslatePoint(new System.Windows.Point(), slider).Y;
        Check(highThumb < lowThumb && track.DecreaseRepeatButton.ActualHeight > track.IncreaseRepeatButton.ActualHeight,
            "Increasing volume moves the thumb upward and fills the vertical track from the bottom");
        if (previewPath is not null)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64, (int)Math.Ceiling(popupContent.ActualHeight),
                96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(popupContent);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(previewPath);
            encoder.Save(stream);
        }
        mute.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeaveEvent
        });
        DispatcherTest.Run(async () =>
        {
            // The timer runs at Background priority; artwork/layout work from earlier UI tests can delay it.
            var deadline = Environment.TickCount64 + 3000;
            while (popup.IsOpen && Environment.TickCount64 < deadline)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(25);
            }
        });
        Check(!popup.IsOpen, "Volume popup closes after leaving the control");
        var controls = new[] { "TransportControls", "MuteButton", "QueueToggle" }
            .Select(name => (System.Windows.FrameworkElement)window.FindName(name))
            .Select(element => element.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(element.RenderSize)))
            .ToArray();
        Check(controls.Zip(controls.Skip(1), (left, right) => left.Right <= right.Left).All(fits => fits)
              && controls[^1].Right <= 884,
            "Volume and queue controls fit without overlap at minimum window width");
        window.DataContext = null;
        window.Close();
        using var backend = new NAudioPlayer();
        backend.Volume = 0.4f;
        backend.Stop();
        Check(backend.Volume == 0.4f, "Audio backend retains volume before loading and after stopping");
        backend.Volume = -1;
        Check(backend.Volume == 0, "Audio backend clamps negative gain");
        backend.Volume = 2;
        backend.Volume = float.NaN;
        Check(backend.Volume == 1, "Audio backend rejects invalid gain");
    }

    private static void CheckTrackMenus()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        var first = Track("First song");
        var second = Track("Second song");
        vm.Tracks.Add(first);
        vm.Tracks.Add(second);
        vm.SelectedTrack = first;
        var view = new MusicPlayer.Views.LibraryView { DataContext = vm };
        view.Measure(new System.Windows.Size(884, 440));
        view.Arrange(new System.Windows.Rect(0, 0, 884, 440));
        view.UpdateLayout();
        var list = (System.Windows.Controls.ListView)view.FindName("TrackList");
        var firstRow = (System.Windows.Controls.ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var secondRow = (System.Windows.Controls.ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        Check(firstRow.ContextMenu is not null && secondRow.ContextMenu is not null,
            "Track rows expose native context menus for right-click and keyboard access");
        var menu = secondRow.ContextMenu!;
        menu.PlacementTarget = secondRow;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var append = (System.Windows.Controls.MenuItem)menu.Items[0];
        var next = (System.Windows.Controls.MenuItem)menu.Items[1];
        Check(ReferenceEquals(append.DataContext, second) && ReferenceEquals(next.DataContext, second),
            "Track menu binds to its row instead of the previous selection");
        append.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Check(vm.Queue.SequenceEqual(new[] { second }) && vm.CurrentTrack is null,
            "Row menu appends its song without starting playback");
        menu = firstRow.ContextMenu!;
        menu.PlacementTarget = firstRow;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ((System.Windows.Controls.MenuItem)menu.Items[1]).RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Check(vm.Queue.SequenceEqual(new[] { first, second }) && vm.CurrentTrack is null,
            "Opening another row menu updates Play next to that song");
        Check(menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => item.Header.ToString() == "Add to _playlist…"),
            "Track menu retains Add to playlist");
        var headerButtons = Descendants(view).OfType<System.Windows.Controls.Button>()
            .Where(button => ItemsControlContainer(button) is null).ToArray();
        Check(headerButtons.Count(button => Equals(button.Content, "Play all")) == 1 &&
              !headerButtons.Any(button => Equals(button.Content, "Add to queue") || Equals(button.Content, "Play next")),
            "Header keeps Play all and removes selected-song queue actions");
        var addMusic = headerButtons.First(button => Equals(button.Content, "Add music…"));
        addMusic.ContextMenu.PlacementTarget = addMusic;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(((System.Windows.Controls.MenuItem)addMusic.ContextMenu.Items[0]).Command, vm.AddMusicFilesCommand) &&
              ReferenceEquals(((System.Windows.Controls.MenuItem)addMusic.ContextMenu.Items[1]).Command, vm.AddMusicFolderCommand),
            "Add music menu binds file and folder imports to the library");

        System.Windows.DependencyObject? ItemsControlContainer(System.Windows.DependencyObject element) =>
            System.Windows.Controls.ItemsControl.ContainerFromElement(list, element);
    }

    private static void CheckQueueVisibility()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MusicPlayerPreferencesTests",
            Guid.NewGuid().ToString("N"), "preferences.json");
        MainViewModel Create() => new(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            uiPreferencesStore: new JsonUiPreferencesStore(path));
        using var vm = Create();
        Check(!vm.IsQueueOpen, "Queue starts collapsed without a saved preference");
        var window = new MainWindow(vm);
        var content = (System.Windows.FrameworkElement)window.Content;
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("QueueToggle");
        var count = (System.Windows.Controls.TextBlock)window.FindName("QueueTrackCount");
        var sidebar = (System.Windows.FrameworkElement)window.FindName("QueueSidebar");
        var page = (System.Windows.FrameworkElement)window.FindName("PageHost");
        void Layout()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            content.Measure(new System.Windows.Size(884, 561));
            content.Arrange(new System.Windows.Rect(0, 0, 884, 561));
            content.UpdateLayout();
        }
        Layout();
        Check(sidebar.Visibility == System.Windows.Visibility.Collapsed && page.ActualWidth == 884,
            "Collapsed queue releases all 296 pixels");
        Check(count.Text == "(0)", "Empty queue count is visible");
        toggle.IsChecked = true;
        Layout();
        Check(vm.IsQueueOpen && sidebar.Visibility == System.Windows.Visibility.Visible && page.ActualWidth == 588,
            "Bottom player toggle opens the 296 pixel queue");
        using (var restored = Create()) Check(restored.IsQueueOpen, "Open preference survives a new view model");
        vm.Queue.Add(Track("A"));
        vm.Queue.Add(Track("A"));
        Layout();
        Check(count.Text == "(2)", "Toggle count includes duplicate upcoming entries");
        toggle.IsChecked = false;
        vm.NextCommand.Execute(null);
        Layout();
        Check(!vm.IsQueueOpen && count.Text == "(1)", "Playback updates count while queue stays collapsed");
        vm.ClearQueueCommand.Execute(null);
        vm.ConfirmClearQueueCommand.Execute(null);
        Layout();
        Check(count.Text == "(0)" && !vm.IsQueueOpen, "Clearing queue updates count without reopening it");
        var toggleBounds = toggle.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(toggle.RenderSize));
        var transport = (System.Windows.FrameworkElement)window.FindName("TransportControls");
        var transportBounds = transport.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(transport.RenderSize));
        Check(toggleBounds.Right <= 884 && toggleBounds.Left >= transportBounds.Right,
            "Queue toggle fits beside playback at minimum window width");
        using (var restored = Create()) Check(!restored.IsQueueOpen, "Closed preference survives a new view model");
        vm.NavigateCommand.Execute(AppPage.Playlists);
        vm.IsQueueOpen = true;
        using (var restored = Create())
            Check(restored.SelectedPage == AppPage.Playlists && restored.IsQueueOpen,
                "Last view survives restart and queue preference writes");
        vm.NavigateCommand.Execute(AppPage.NowPlaying);
        using (var restored = Create())
            Check(restored.SelectedPage == AppPage.NowPlaying && restored.IsQueueOpen,
                "Navigation persists without overwriting the queue preference");
        vm.NavigateCommand.Execute(AppPage.Library);
        using (var restored = Create()) Check(restored.SelectedPage == AppPage.Library, "Returning Library is persisted");
        System.IO.File.WriteAllText(path, "{\"IsQueueOpen\":true}");
        using (var restored = Create())
            Check(restored.SelectedPage == AppPage.Library && restored.IsQueueOpen,
                "Existing queue-only preferences remain compatible");
        System.IO.File.WriteAllText(path, "{\"IsQueueOpen\":true,\"SelectedPage\":999}");
        using (var restored = Create())
            Check(restored.SelectedPage == AppPage.Library && restored.IsQueueOpen,
                "Unknown saved views fall back to Library while retaining the queue preference");
        System.IO.File.WriteAllText(path, "invalid json");
        using (var restored = Create())
            Check(!restored.IsQueueOpen && restored.SelectedPage == AppPage.Library,
                "Invalid preferences safely use collapsed queue and Library defaults");
        window.DataContext = null;
        window.Close();
    }

    private static void CheckPlaylistsAndNavigation()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MusicPlayerPlaylistTests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "music.db");
        IPlaylistStore store = new SqliteMusicStore(path);
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, playlistStore: store);
        Check(vm.SelectedPage == AppPage.Library && vm.Playlists.Count == 0, "Library is the initial view");
        vm.CreatePlaylistCommand.Execute(null);
        Check(vm.SelectedPage == AppPage.Playlists && vm.SelectedPlaylist is not null, "Create selects a playlist and opens its view");
        vm.PlaylistName = "  Evening listening  ";
        vm.RenamePlaylistCommand.Execute(null);
        Check(vm.SelectedPlaylist!.Name == "Evening listening", "Playlist rename trims whitespace");
        vm.PlaylistName = "   ";
        Check(!vm.RenamePlaylistCommand.CanExecute(null), "Blank playlist names are rejected");
        var a = Track("A"); var b = Track("B");
        vm.SelectedTrack = a;
        vm.AddSelectedTrackToPlaylistCommand.Execute(null);
        vm.AddSelectedTrackToPlaylistCommand.Execute(null);
        vm.SelectedTrack = b;
        vm.AddSelectedTrackToPlaylistCommand.Execute(null);
        Check(vm.SelectedPlaylist.Tracks.SequenceEqual(new[] { a, a, b }), "Adding to playlists preserves duplicate entries");
        Check(vm.SelectedPlaylist.CoverTracks.SequenceEqual(new[] { a, b }), "Playlist covers choose distinct songs");
        var coverChanges = 0;
        vm.SelectedPlaylist.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Playlist.CoverTracks)) coverChanges++; };
        vm.SelectedPlaylistTrackIndex = 2;
        vm.MovePlaylistTrackUpCommand.Execute(null);
        Check(vm.SelectedPlaylist.Tracks.SequenceEqual(new[] { a, b, a }) && vm.SelectedPlaylistTrackIndex == 1, "Playlist tracks can be reordered");
        Check(coverChanges > 0, "Playlist covers refresh when tracks change");
        vm.RemovePlaylistTrackCommand.Execute(null);
        Check(vm.SelectedPlaylist.Tracks.SequenceEqual(new[] { a, a }), "Removing affects only the selected playlist entry");
        vm.PlayPlaylistCommand.Execute(null);
        vm.ClosePlaylistCommand.Execute(null);
        Check(!vm.IsPlaylistOpen, "Back returns to the playlist gallery");
        vm.OpenPlaylistCommand.Execute(vm.SelectedPlaylist);
        Check(vm.IsPlaylistOpen && !vm.IsRenamingPlaylist, "Opening a cover displays playlist details");
        var playCount = player.PlayCount;
        vm.NavigateCommand.Execute(AppPage.NowPlaying);
        vm.NavigateCommand.Execute(AppPage.Library);
        vm.NavigateCommand.Execute(AppPage.Playlists);
        Check(vm.CurrentTrack == a && vm.Queue.Single() == a && vm.IsPlaying && player.PlayCount == playCount,
            "Navigation preserves active playback and queue");
        vm.QueuePlaylistCommand.Execute(null);
        Check(vm.Queue.Count == 3 && player.PlayCount == playCount, "Queue playlist appends without interrupting playback");
        using var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(), playlistStore: store);
        Check(restored.Playlists.Single().Name == "Evening listening" && restored.SelectedPlaylist!.Tracks.Count == 2 &&
              restored.SelectedPlaylist.Tracks.All(t => t.Title == "A"), "Playlists persist with metadata and duplicates");
        var savedId = restored.SelectedPlaylist!.Id;
        Check(savedId == vm.SelectedPlaylist.Id, "Playlist identity persists across restarts");
        restored.DeletePlaylistCommand.Execute(null);
        restored.ConfirmDeletePlaylistCommand.Execute(null);
        Check(store.Load().Count == 0 && restored.SelectedPlaylist is null, "Deleting a playlist persists without touching music files");
        var corrupt = System.IO.Path.Combine(directory, "corrupt.db");
        System.IO.File.WriteAllText(corrupt, "not a database");
        using var damaged = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(), playlistStore: new SqliteMusicStore(corrupt));
        using var corruptReader = new System.IO.StreamReader(System.IO.File.Open(corrupt, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite));
        Check(damaged.PlaylistError is not null && !damaged.CreatePlaylistCommand.CanExecute(null) &&
              corruptReader.ReadToEnd() == "not a database", "Invalid playlist databases are reported and preserved");
    }

    private static void CheckShuffleAndRepeat()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, new Random(42));
        var a = Track("A");
        var b = Track("B");
        var c = Track("C");
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(b);
        vm.Queue.Add(c);
        Check(vm.RepeatMode == PlaybackRepeatMode.Off && !vm.IsShuffleEnabled, "Modes default to off");
        vm.CycleRepeatCommand.Execute(null);
        Check(vm.RepeatMode == PlaybackRepeatMode.All && vm.IsRepeatEnabled, "Repeat cycles to all");
        player.End();
        Check(vm.CurrentTrack == b && vm.Queue.SequenceEqual(new[] { c, a }), "Repeat all recycles completed track");
        player.End();
        player.End();
        Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, c }), "Repeat all loops the complete queue");
        vm.CycleRepeatCommand.Execute(null);
        var plays = player.PlayCount;
        player.End();
        Check(vm.RepeatMode == PlaybackRepeatMode.One && vm.CurrentTrack == a &&
              player.PlayCount == plays + 1 && player.Position == TimeSpan.Zero &&
              vm.Queue.SequenceEqual(new[] { b, c }), "Repeat one restarts without consuming the queue");
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue.Single() == c, "Manual next bypasses repeat one");
        vm.CycleRepeatCommand.Execute(null);
        player.End();
        player.End();
        Check(vm.RepeatMode == PlaybackRepeatMode.Off && !vm.IsPlaying && vm.IsQueueEmpty, "Repeat off exhausts queue normally");
        vm.CycleRepeatCommand.Execute(null);
        Check(vm.NextCommand.CanExecute(null), "Repeat all enables next for a single track");
        plays = player.PlayCount;
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack == c && player.PlayCount == plays + 1, "Repeat all handles a single track");
        vm.Queue.Add(b);
        vm.ClearQueueCommand.Execute(null);
        vm.ConfirmClearQueueCommand.Execute(null);
        player.End();
        Check(vm.CurrentTrack == c && vm.IsQueueEmpty, "Clear does not resurrect removed songs with repeat all");
        plays = player.PlayCount;
        vm.PauseCommand.Execute(null);
        vm.StopCommand.Execute(null);
        Check(player.PlayCount == plays && !vm.IsPlaying, "Pause and stop do not trigger repeat");

        vm.Tracks.Add(a);
        vm.Tracks.Add(b);
        vm.PlayAllCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.Single() == b, "Play all starts a fresh repeat session");
        vm.Queue.Add(Track("broken"));
        player.End();
        player.End();
        Check(vm.CurrentTrack == a && vm.Queue.Single() == b, "Repeat all discards unreadable entries");
        player.FailAllLoads = true;
        player.End();
        Check(vm.CurrentTrack is null && vm.IsQueueEmpty && !vm.IsPlaying && vm.PlaybackError is not null,
            "All failed files terminate repeat without an infinite loop");
        player.FailAllLoads = false;
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(b);
        vm.CycleRepeatCommand.Execute(null);
        player.FailAllLoads = true;
        player.End();
        Check(vm.CurrentTrack is null && vm.IsQueueEmpty && !vm.IsPlaying, "Repeat one load failure terminates safely");
        player.FailAllLoads = false;
        vm.CycleRepeatCommand.Execute(null);

        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        var entries = Enumerable.Range(0, 12).Select(i => Track($"Track {i}")).ToList();
        entries.Add(entries[0]);
        foreach (var track in entries) vm.Queue.Add(track);
        vm.SelectedQueueIndex = 4;
        var selected = vm.Queue[4];
        plays = player.PlayCount;
        vm.ToggleShuffleCommand.Execute(null);
        Check(vm.IsShuffleEnabled && !vm.Queue.SequenceEqual(entries) && vm.Queue.Count == entries.Count &&
              entries.All(t => entries.Count(x => x == t) == vm.Queue.Count(x => x == t)),
            "Shuffle permutes upcoming entries and preserves duplicates");
        Check(vm.CurrentTrack == a && player.PlayCount == plays && vm.Queue[vm.SelectedQueueIndex] == selected,
            "Shuffle preserves current playback and queue selection");
        vm.SelectedTrack = c;
        vm.PlayNextCommand.Execute(null);
        player.End();
        Check(vm.CurrentTrack == c, "Play next takes priority while shuffle is enabled");
        var visibleOrder = vm.Queue.ToArray();
        foreach (var expected in visibleOrder)
        {
            player.End();
            Check(vm.CurrentTrack == expected, $"Shuffle plays the displayed next track: {expected.Title}");
        }
        player.End();
        Check(!vm.IsPlaying && vm.IsQueueEmpty, "Shuffle without repeat stops after one pass");
        vm.Tracks.Clear();
        foreach (var track in entries) vm.Tracks.Add(track);
        vm.CycleRepeatCommand.Execute(null);
        vm.PlayAllCommand.Execute(null);
        var cycle = new[] { vm.CurrentTrack! }.Concat(vm.Queue).ToArray();
        Check(cycle.Length == entries.Count && !cycle.SequenceEqual(entries) &&
              entries.All(t => entries.Count(x => x == t) == cycle.Count(x => x == t)), "Play all honors shuffle");
        foreach (var expected in cycle.Skip(1).Append(cycle[0]))
        {
            player.End();
            Check(vm.CurrentTrack == expected, $"Shuffle and repeat all retain the visible cycle: {expected.Title}");
        }
        visibleOrder = vm.Queue.ToArray();
        vm.ToggleShuffleCommand.Execute(null);
        Check(!vm.IsShuffleEnabled && vm.Queue.SequenceEqual(visibleOrder), "Turning shuffle off preserves visible order");
    }

    private static void CheckPrevious()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var a = Track("A");
        var b = Track("B");
        var c = Track("C");
        Check(!vm.PreviousCommand.CanExecute(null), "Previous is disabled without playback");
        vm.Tracks.Add(a); vm.Tracks.Add(b); vm.Tracks.Add(c);
        vm.PlayAllCommand.Execute(null);
        vm.NextCommand.Execute(null);
        player.Seek(TimeSpan.FromSeconds(12));
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == b && player.Position == TimeSpan.Zero && vm.Queue.Single() == c,
            "Previous restarts a track after three seconds");
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, c }), "Previous returns to history and preserves forward order");
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue.Single() == c, "Next returns to the interrupted track");
        vm.RepeatMode = PlaybackRepeatMode.One;
        player.End();
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, c }), "Repeat one does not pollute previous history");
        vm.RepeatMode = PlaybackRepeatMode.All;
        vm.PlayAllCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.Queue.SequenceEqual(new[] { b, c }), "Previous preserves repeat-all cycle size");
        vm.NextCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue.SequenceEqual(new[] { c, a }), "Next after previous keeps repeat-all order");
        vm.IsShuffleEnabled = true;
        var expected = vm.Queue[0];
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue[0] == expected, "Previous follows actual shuffled playback history");
    }

    private static void CheckLargePlaylistLayout()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        var artwork = PlaylistArtworkTests.Artwork(System.Windows.Media.Colors.SteelBlue);
        var playlist = new Playlist(Enumerable.Range(0, 10_000).Select(i => new Track
        {
            FilePath = $"virtual-track-{i}", Title = $"Track {i}", Artist = "Artist", Album = "Album", ArtworkData = artwork
        }));
        vm.Playlists.Add(playlist);
        vm.OpenPlaylistCommand.Execute(playlist);
        vm.SelectedPage = AppPage.Playlists;
        var window = new MainWindow(vm);
        var content = (System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(900, 560));
        content.Arrange(new System.Windows.Rect(0, 0, 900, 560));
        content.UpdateLayout();
        var list = Descendants(content).OfType<System.Windows.Controls.ListBox>().Single(l => l.Name == "PlaylistTracks");
        Check(Descendants(list).OfType<System.Windows.Controls.ListBoxItem>().Count() is > 0 and < 100,
            "A 10,000-track playlist realizes only visible rows and a small scroll cache");
        list.ScrollIntoView(playlist.Tracks[^1]);
        content.UpdateLayout();
        Check(list.ItemContainerGenerator.ContainerFromIndex(9_999) is not null &&
              Descendants(list).OfType<System.Windows.Controls.ListBoxItem>().Count() < 100,
            "Scrolling to the end of a large playlist keeps row virtualization active");

        var image = new MusicPlayer.Controls.TrackArtwork { Track = playlist.Tracks[0] };
        PumpUntil(image.ArtworkReady);
        Check(image.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true, PixelWidth: <= 72 },
            "Track artwork loads a frozen, thumbnail-sized image asynchronously");
        image.Track = new Track { FilePath = "red", Title = "Red", ArtworkData = PlaylistArtworkTests.Artwork(System.Windows.Media.Colors.IndianRed) };
        var staleLoad = image.ArtworkReady;
        image.Track = null;
        PumpUntil(staleLoad);
        Check(image.Source is null, "Recycled rows ignore stale artwork loads");
        image.Track = new Track { FilePath = "broken", Title = "Broken", ArtworkData = [0, 1, 2] };
        PumpUntil(image.ArtworkReady);
        Check(image.Source is null, "Corrupt row artwork preserves the placeholder");
        window.DataContext = null;
        window.Close();
    }

    private static void CheckRestoredPlaylistArtwork()
    {
        using var temporaryDirectory = new TemporaryTestDirectory("MusicPlayerArtworkRestartTests");
        var directory = temporaryDirectory.Path;
        FixtureLibrary.Write(directory);
        var artwork = PlaylistArtworkTests.Artwork(System.Windows.Media.Colors.SeaGreen);
        foreach (var (name, data) in new[] { ("01.wav", artwork), ("03.wav", new byte[] { 0, 1, 2 }) })
        {
            using var file = TagLib.File.Create(System.IO.Path.Combine(directory, name));
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(data)) { MimeType = "image/png" }];
            file.Save();
        }
        var metadata = new TagLibMetadataService();
        var playlist = new Playlist(new[] { "01.wav", "02.wav", "03.wav" }
            .Select(name => metadata.ReadTrack(System.IO.Path.Combine(directory, name))));
        playlist.Tracks.Add(new Track { FilePath = System.IO.Path.Combine(directory, "missing.wav"), Title = "Missing" });
        var savedPath = System.IO.Path.Combine(directory, "music.db");
        new SqliteMusicStore(savedPath).SavePlaylists([playlist]);

        using var vm = new MainViewModel(new Picker(), new Picker(), metadata, new Scanner(), new FakePlayer(),
            playlistStore: new SqliteMusicStore(savedPath));
        var restored = vm.Playlists.Single();
        Check(restored.Tracks.All(t => t.ArtworkData is null), "Restart fixture restores tracks without in-memory artwork");
        vm.OpenPlaylistCommand.Execute(restored);
        vm.SelectedPage = AppPage.Playlists;
        var window = new MainWindow(vm);
        try
        {
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(1280, 780));
            content.Arrange(new System.Windows.Rect(0, 0, 1280, 780));
            content.UpdateLayout();
            var rows = Descendants(content).OfType<MusicPlayer.Controls.TrackArtwork>().ToArray();
            Check(rows.Length == 4, "Restored playlist rows bind to the artwork control");
            PumpUntil(Task.WhenAll(rows.Select(row => row.ArtworkReady)));
            var first = rows.Single(row => ReferenceEquals(row.Track, restored.Tracks[0]));
            Check(first.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true, PixelWidth: <= 72 },
                "Reopening saved playlists loads embedded row artwork from disk without reimporting or playing");
            Check(rows.Where(row => row != first).All(row => row.Source is null),
                "Restored tracks with absent artwork, corrupt pictures, or missing files keep their placeholders");
            var reused = new MusicPlayer.Controls.TrackArtwork { Track = restored.Tracks[0] };
            PumpUntil(reused.ArtworkReady);
            Check(ReferenceEquals(reused.Source, first.Source), "Recreated rows reuse the restored track thumbnail");
            reused.Track = new Track { FilePath = restored.Tracks[0].FilePath, Title = "Pending cover" };
            var staleLoad = reused.ArtworkReady;
            reused.Track = null;
            PumpUntil(staleLoad);
            Check(reused.Source is null, "Recycled rows ignore stale disk artwork loads");
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }

    private static void PumpUntil(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void Render(MainWindow window, string path, int width, int height)
    {
        var preview = new MainWindow((MainViewModel)window.DataContext);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (System.Windows.FrameworkElement)preview.Content;
        content.SetValue(System.Windows.Controls.Panel.BackgroundProperty, window.Background);
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        var playButton = (System.Windows.FrameworkElement)preview.FindName("PlayPauseButton");
        var playCenter = playButton.TransformToAncestor(content).Transform(new System.Windows.Point(playButton.ActualWidth / 2, 0)).X;
        Check(Math.Abs(playCenter - width / 2d) <= 1, $"Playback is centered at {width}px");
        var seekSlider = (System.Windows.FrameworkElement)preview.FindName("SeekSlider");
        Check(seekSlider.ActualWidth >= width * 0.85, $"Timeline spans the player at {width}px");
        var page = ((MainViewModel)preview.DataContext).SelectedPage;
        var progressPanel = (System.Windows.FrameworkElement)preview.FindName("ImportProgressPanel");
        var isImporting = ((MainViewModel)preview.DataContext).IsImportingPlaylist;
        Check((progressPanel.Visibility == System.Windows.Visibility.Visible) == isImporting, "Import modal follows the active import");
        Check(((System.Windows.FrameworkElement)preview.FindName("MainContent")).IsEnabled != isImporting,
            "Import modal blocks interaction with the underlying window");
        if (isImporting)
        {
            Check(progressPanel.ActualWidth == width && progressPanel.ActualHeight == height,
                "Import modal overlay covers the whole window");
            Check(((System.Windows.Shapes.Path)preview.FindName("ImportSpinner")).RenderTransform.HasAnimatedProperties,
                "Import spinner animates during discovery and track processing");
            var progressBar = (System.Windows.Controls.ProgressBar)preview.FindName("PlaylistImportProgressBar");
            Check(progressBar.IsIndeterminate == ((MainViewModel)preview.DataContext).IsImportDiscovering &&
                  Math.Abs(progressBar.Value - ((MainViewModel)preview.DataContext).ImportProgressPercent) < 0.01,
                "Visible progress bar tracks discovery and import percentage");
        }
        var expectedView = page switch { AppPage.Playlists => typeof(MusicPlayer.Views.PlaylistsView), AppPage.NowPlaying => typeof(MusicPlayer.Views.NowPlayingView), _ => typeof(MusicPlayer.Views.LibraryView) };
        Check(Descendants(content).Any(v => v.GetType() == expectedView), $"{page} view renders at {width}px");
        if (page == AppPage.Library && ((MainViewModel)preview.DataContext).Tracks.Count > 0)
        {
            var list = Descendants(content).OfType<System.Windows.Controls.ListView>().First(v => v.Name == "TrackList");
            foreach (var (headerName, cellName) in new[] { ("TitleHeader", "TrackTitle"), ("ArtistHeader", "TrackArtist") })
            {
                var header = Descendants(content).OfType<System.Windows.FrameworkElement>().First(v => v.Name == headerName);
                var cell = Descendants(list).OfType<System.Windows.Controls.TextBlock>().First(t => t.Name == cellName);
                var headerX = header.TransformToAncestor(content).Transform(new System.Windows.Point()).X;
                var cellX = cell.TransformToAncestor(content).Transform(new System.Windows.Point()).X;
                Check(Math.Abs(headerX - cellX) <= 1, $"{headerName} aligns with rows at {width}px");
            }
            var scroll = Descendants(list).OfType<System.Windows.Controls.ScrollViewer>().First();
            if (scroll.ScrollableHeight > 0)
            {
                scroll.ScrollToEnd();
                content.UpdateLayout();
                Check(scroll.VerticalOffset > 0, $"Library scrolls at {width}px");
                scroll.ScrollToHome();
                content.UpdateLayout();
            }
        }
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
        var artworkReady = Task.WhenAll(Descendants(content).OfType<MusicPlayer.Controls.PlaylistCover>().Select(c => c.ArtworkReady));
        if (!artworkReady.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _ = artworkReady.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        artworkReady.GetAwaiter().GetResult();
        content.UpdateLayout();
        foreach (var cover in Descendants(content).OfType<MusicPlayer.Controls.PlaylistCover>())
        {
            if (cover.Tracks is { Count: 4 } coverTracks && coverTracks.All(t => t.ArtworkData is { Length: > 0 }))
                Check(((System.Windows.Controls.ItemsControl)cover.FindName("CoverImages")).Items.Count == 4,
                    "Playlist thumbnail combines four song covers");
        }
        if (page == AppPage.Playlists && ((MainViewModel)preview.DataContext).IsPlaylistOpen)
        {
            var detail = Descendants(content).OfType<System.Windows.Controls.Grid>().Single(g => g.Name == "PlaylistDetail");
            var list = Descendants(detail).OfType<System.Windows.Controls.ListBox>().Single(s => s.Name == "PlaylistTracks");
            var scroll = Descendants(list).OfType<System.Windows.Controls.ScrollViewer>().Single();
            if (scroll.ScrollableHeight > 0)
            {
                scroll.ScrollToEnd();
                content.UpdateLayout();
                Check(scroll.VerticalOffset > 0, "Playlist tracks scroll in small windows");
                scroll.ScrollToHome();
                content.UpdateLayout();
            }
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        preview.DataContext = null;
        preview.Close();
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Track Track(string title) => new() { Title = title, FilePath = title, Duration = TimeSpan.FromMinutes(3) };
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        public float Volume { get; set; } = 1f;
        public event EventHandler? PlaybackEnded;
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int PlayCount { get; private set; }
        public bool FailAllLoads { get; set; }
        public void Load(string path)
        {
            if (FailAllLoads || path == "broken") throw new System.IO.IOException("Missing file");
            Position = TimeSpan.Zero;
        }
        public void Play() => PlayCount++;
        public void Pause() { }
        public void Stop() => Position = TimeSpan.Zero;
        public void Seek(TimeSpan position) => Position = position;
        public void End() { Position = Duration; PlaybackEnded?.Invoke(this, EventArgs.Empty); }
    }
    private sealed class Picker : IFilePickerService, IFolderPickerService
    {
        public IReadOnlyList<string> PickAudioFiles() => [];
        public string? PickAudioFile() => null;
        public string? PickPlaylistFile() => null;
        public string? PickMusicFolder() => null;
    }
    private sealed class Metadata : IMetadataService
    {
        public Track ReadTrack(string path) => Track(path);
    }
    private sealed class Scanner : ILibraryScanner
    {
        public Task<IReadOnlyList<Track>> ScanAsync(string path) => Task.FromResult<IReadOnlyList<Track>>([]);
    }
}
