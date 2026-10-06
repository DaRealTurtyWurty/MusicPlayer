using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckLyricsViewModelAsync()
    {
        await CheckLyricsControlsAsync();
        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsViewTests");
        var audio = Path.Combine(temporary.Path, "Song.flac");
        var path = Path.ChangeExtension(audio, ".lrc");
        await File.WriteAllTextAsync(path, "[offset:250]\n[00:01]First\n[00:04]\n[00:06]Last");
        var preferences = new JsonUiPreferencesStore(Path.Combine(temporary.Path, "preferences.json"));
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            uiPreferencesStore: preferences, monitorLibrary: false);
        Check(vm.Lyrics.IsEnabled && vm.Lyrics.StatusTitle == "Nothing playing", "Lyrics default on with a no-track state");
        var track = new Track { FilePath = audio, Title = "Song", Duration = player.Duration };
        vm.Queue.Add(track);
        vm.PlayCommand.Execute(null);
        await vm.Lyrics.LoadingTask;
        Check(vm.Lyrics.HasLyrics && vm.Lyrics.Lines.Count == 3 && vm.Lyrics.ActiveLine is null,
            "Starting a track automatically loads its sidecar without highlighting the first line early");
        vm.PositionSeconds = 0.75;
        Check(vm.Lyrics.ActiveLine == vm.Lyrics.Lines[0], "Playback position applies the LRC offset once");
        vm.PositionSeconds = 4;
        Check(vm.Lyrics.ActiveLine?.Text == "•••" && !vm.Lyrics.Lines[0].IsActive, "Instrumental cues clear the previous lyric");
        vm.PositionSeconds = 7;
        Check(vm.Lyrics.ActiveLine == vm.Lyrics.Lines[2], "Forward seek selects the correct lyric");
        vm.PauseCommand.Execute(null);
        var playCount = player.PlayCount;
        vm.Lyrics.SeekToLineCommand.Execute(vm.Lyrics.Lines[0]);
        Check(vm.PositionSeconds == 0.75 && player.Position.TotalSeconds == 0.75 && !vm.IsPlaying && player.PlayCount == playCount,
            "Clicking a lyric seeks with the inverse offset and preserves pause state");
        player.Seek(TimeSpan.FromSeconds(7));
        await Task.Delay(350);
        Check(vm.Lyrics.ActiveLine == vm.Lyrics.Lines[2], "The existing playback timer updates lyric selection");
        vm.StopCommand.Execute(null);
        Check(vm.Lyrics.ActiveLine is null, "Stop resets lyric selection");
        vm.PositionSeconds = 180;
        Check(vm.Lyrics.ActiveLine is null, "Lyrics clear at track end");
        vm.PositionSeconds = 7;
        vm.RepeatMode = PlaybackRepeatMode.One;
        player.End();
        Check(vm.Lyrics.ActiveLine is null && vm.PositionSeconds == 0, "Repeat-one resets lyrics even when the track reference is unchanged");

        vm.Lyrics.ToggleCommand.Execute(null);
        Check(!vm.Lyrics.IsEnabled && !vm.Lyrics.HasLyrics && !preferences.LoadLyricsEnabled(), "Hiding lyrics clears the panel and persists the preference");
        preferences.SaveVolume(35, 35);
        preferences.SaveQueueOpen(true);
        Check(!preferences.LoadLyricsEnabled(), "Other preference saves preserve lyrics visibility");
        using (var restored = new LyricsViewModel(new LocalLyricsSource(), new JsonUiPreferencesStore(Path.Combine(temporary.Path, "preferences.json"))))
            Check(!restored.IsEnabled, "Lyrics visibility survives restart");
        vm.Lyrics.ToggleCommand.Execute(null);
        await vm.Lyrics.LoadingTask;
        Check(vm.Lyrics.HasLyrics && preferences.LoadVolume().Volume == 35 && preferences.LoadQueueOpen(),
            "Showing lyrics reloads the file without changing other preferences");
        await File.WriteAllTextAsync(path, "[00:01]Edited\ninvalid");
        vm.Lyrics.ReloadCommand.Execute(null);
        await vm.Lyrics.LoadingTask;
        Check(vm.Lyrics.Lines[0].Text == "Edited" && vm.Lyrics.Warning is not null, "Reload picks up edits and shows parser warnings");
        vm.CurrentTrack = Track("Missing lyrics");
        await vm.Lyrics.LoadingTask;
        Check(!vm.Lyrics.HasLyrics && vm.Lyrics.StatusTitle == "No local lyrics" && vm.PlaybackError is null,
            "Track changes clear stale lyrics and missing files do not become playback errors");
        vm.CurrentTrack = track;
        await File.WriteAllTextAsync(path, "metadata only");
        await vm.Lyrics.LoadingTask;
        vm.Lyrics.ReloadCommand.Execute(null);
        await vm.Lyrics.LoadingTask;
        Check(vm.Lyrics.StatusTitle == "No usable lyrics", "Invalid files have a distinct empty state");

        var deferred = new DeferredLyricsSource();
        using var lyrics = new LyricsViewModel(deferred);
        lyrics.SetTrack(Track("A"), TimeSpan.FromMinutes(3));
        var oldTask = lyrics.LoadingTask;
        lyrics.SetTrack(Track("B"), TimeSpan.FromMinutes(3));
        deferred.Requests[1].Complete("B");
        await lyrics.LoadingTask;
        deferred.Requests[0].Complete("A");
        await oldTask;
        Check(lyrics.Lines[0].Text == "B" && deferred.Requests[0].Token.IsCancellationRequested,
            "Late completion from a cancelled track cannot replace the current lyrics");
        lyrics.ReloadCommand.Execute(null);
        var hiddenTask = lyrics.LoadingTask;
        lyrics.IsEnabled = false;
        deferred.Requests[2].Complete("Hidden");
        await hiddenTask;
        Check(!lyrics.HasLyrics && !lyrics.IsLoading, "Hiding lyrics during a load prevents its result from reappearing");
        lyrics.IsEnabled = true;
        var disposedTask = lyrics.LoadingTask;
        lyrics.Dispose();
        deferred.Requests[3].Complete("Disposed");
        await disposedTask;
        Check(!lyrics.HasLyrics, "Disposal cancels pending loads without publishing results");
        using var failure = new LyricsViewModel(new FailingLyricsSource());
        failure.SetTrack(track, track.Duration);
        await failure.LoadingTask;
        Check(failure.StatusTitle == "Could not load lyrics" && !failure.IsLoading, "Unexpected source failures remain within the lyrics panel");
        Console.WriteLine("Lyrics view-model tests passed.");
    }

    private sealed class LyricsPicker : ILyricsFilePicker
    {
        public string? Path { get; set; }
        public string? PickLyricsFile() => Path;
    }

    private static async Task CheckLyricsControlsAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsControlsTests");
        var audio = Path.Combine(temporary.Path, "Song.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[offset:250]\n[00:01]First\n[00:04]Second");
        var selected = Path.Combine(temporary.Path, "Other.lrc");
        await File.WriteAllTextAsync(selected, "[00:02]Selected");
        var preferencesPath = Path.Combine(temporary.Path, "preferences.json");
        var preferences = new JsonUiPreferencesStore(preferencesPath);
        var picker = new LyricsPicker();
        using var lyrics = new LyricsViewModel(new LocalLyricsSource(), preferences, picker);
        Check(!lyrics.ChooseFileCommand.CanExecute(null), "Sidecar selection is unavailable without a track");
        var track = new Track { FilePath = audio, Title = "Song", Duration = TimeSpan.FromMinutes(3) };
        lyrics.SetTrack(track, track.Duration);
        await lyrics.LoadingTask;
        lyrics.UpdatePosition(0.5);
        lyrics.ShowEarlierCommand.Execute(null);
        lyrics.ShowEarlierCommand.Execute(null);
        lyrics.ShowEarlierCommand.Execute(null);
        Check(lyrics.TimingAdjustmentSeconds == 0.3 && lyrics.ActiveLine == lyrics.Lines[0],
            "Timing controls immediately refresh paused highlighting and add to the file offset");
        double seek = -1;
        lyrics.SeekRequested += seconds => seek = seconds;
        lyrics.SeekToLineCommand.Execute(lyrics.Lines[0]);
        Check(Math.Abs(seek - 0.45) < 0.00001, "Seeking inverts both the source offset and the timing adjustment");
        lyrics.TimingAdjustmentSeconds = 30;
        lyrics.SeekToLineCommand.Execute(lyrics.Lines[0]);
        Check(seek == 0, "Adjusted lyric seeking clamps to the track start");
        lyrics.ShowEarlierCommand.Execute(null);
        Check(lyrics.TimingAdjustmentSeconds == 30, "Timing adjustment is bounded");
        lyrics.TimingAdjustmentSeconds = double.NaN;
        Check(lyrics.TimingAdjustmentSeconds == 0, "Invalid timing adjustments reset safely");
        lyrics.ShowLaterCommand.Execute(null);
        Check(lyrics.TimingAdjustmentSeconds == -0.1, "Later delays lyrics by one tenth of a second");
        lyrics.ResetTimingCommand.Execute(null);

        lyrics.FontSize = 34;
        preferences.SaveVolume(35, 35);
        using (var restored = new LyricsViewModel(new LocalLyricsSource(), new JsonUiPreferencesStore(preferencesPath)))
            Check(restored.FontSize == 34, "Lyrics font size persists across restart and unrelated preference saves");
        lyrics.FontSize = 100;
        Check(lyrics.FontSize == 40, "Font size is bounded");
        lyrics.FontSize = double.PositiveInfinity;
        Check(lyrics.FontSize == 25 && preferences.LoadLyricsFontSize() == 25, "Invalid font size restores and saves the default");

        var existingLoad = lyrics.LoadingTask;
        lyrics.ChooseFileCommand.Execute(null);
        Check(lyrics.LoadingTask == existingLoad && lyrics.SelectedLyricsFilePath is null, "Cancelling the picker leaves lyrics untouched");
        picker.Path = selected;
        lyrics.ChooseFileCommand.Execute(null);
        await lyrics.LoadingTask;
        Check(lyrics.Lines[0].Text == "Selected" && lyrics.SourceLabel == "Other.lrc" && lyrics.UseAutomaticSourceCommand.CanExecute(null),
            "Manual selection loads and identifies a sidecar with a different name");
        await File.WriteAllTextAsync(selected, "[00:02]Edited selection");
        lyrics.ReloadCommand.Execute(null);
        await lyrics.LoadingTask;
        Check(lyrics.Lines[0].Text == "Edited selection", "Reload retains the chosen sidecar");
        lyrics.IsEnabled = false;
        Check(!lyrics.ChooseFileCommand.CanExecute(null), "Hidden lyrics disable sidecar selection");
        lyrics.IsEnabled = true;
        await lyrics.LoadingTask;
        Check(lyrics.Lines[0].Text == "Edited selection", "Toggling lyrics preserves the selected sidecar");
        lyrics.UseAutomaticSourceCommand.Execute(null);
        await lyrics.LoadingTask;
        Check(lyrics.Lines[0].Text == "First" && lyrics.SelectedLyricsFilePath is null, "Automatic restores discovery");
        picker.Path = Path.Combine(temporary.Path, "Missing.lrc");
        lyrics.ChooseFileCommand.Execute(null);
        await lyrics.LoadingTask;
        Check(!lyrics.HasLyrics && lyrics.StatusTitle == "Lyrics file not found", "An unavailable explicit file does not silently load a different source");
        lyrics.TimingAdjustmentSeconds = 1;
        lyrics.SetTrack(new Track { FilePath = Path.Combine(temporary.Path, "Next.flac"), Title = "Next" }, track.Duration);
        await lyrics.LoadingTask;
        Check(lyrics.SelectedLyricsFilePath is null && lyrics.TimingAdjustmentSeconds == 0,
            "Track changes reset sidecar selection and timing adjustment");
        var deferred = new DeferredLyricsSource();
        picker.Path = selected;
        using var switching = new LyricsViewModel(deferred, filePicker: picker);
        switching.SetTrack(track, track.Duration);
        var automaticLoad = switching.LoadingTask;
        switching.ChooseFileCommand.Execute(null);
        Check(deferred.Requests[1].LyricsFilePath == selected, "The selected path is passed to the lyrics source");
        deferred.Requests[1].Complete("Manual");
        await switching.LoadingTask;
        deferred.Requests[0].Complete("Automatic");
        await automaticLoad;
        Check(switching.Lines[0].Text == "Manual" && deferred.Requests[0].Token.IsCancellationRequested,
            "A late automatic load cannot overwrite manual selection for the same track");
        lyrics.Dispose();
        Check(!lyrics.ChooseFileCommand.CanExecute(null), "Disposed lyrics disable file selection");
    }

    private sealed class DeferredLyricsSource : ILocalLyricsSource
    {
        public List<DeferredLyricsRequest> Requests { get; } = [];
        public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null,
            string? lyricsFilePath = null, CancellationToken cancellationToken = default)
        {
            var request = new DeferredLyricsRequest(cancellationToken, lyricsFilePath);
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed class DeferredLyricsRequest(CancellationToken token, string? lyricsFilePath)
    {
        public CancellationToken Token { get; } = token;
        public string? LyricsFilePath { get; } = lyricsFilePath;
        public TaskCompletionSource<LocalLyricsResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(string text) => Completion.SetResult(new(LocalLyricsStatus.Loaded, null,
            new LrcParser().Parse("[00:01]" + text)));
    }

    private sealed class FailingLyricsSource : ILocalLyricsSource
    {
        public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null,
            string? lyricsFilePath = null, CancellationToken cancellationToken = default) => throw new IOException("Test failure");
    }

    private static async Task CheckLyricsViewAsync()
    {
        await CheckLyricsViewModelAsync();
        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsRenderTests");
        var audio = Path.Combine(temporary.Path, "Evening.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"),
            "[00:01]The city settles into blue\n[00:05]And every road leads back to you\n[00:10]We let the quiet fill the room\n" +
            "[00:15]A little light against the gloom\n[00:20]\n[00:25]The night unfolds, the windows glow\n[00:30]There is no hurry now to go");
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(), monitorLibrary: false);
        vm.CurrentTrack = new Track { FilePath = audio, Title = "Evening", Artist = "The Paper Lanterns", Album = "After the Rain", Duration = TimeSpan.FromMinutes(3) };
        vm.DurationSeconds = 180;
        await vm.Lyrics.LoadingTask;
        vm.PositionSeconds = 10;
        var view = new NowPlayingView { DataContext = vm, Background = (Brush)Application.Current.FindResource("CanvasBrush") };
        // A real, offscreen presentation source exercises visibility and animation lifetimes.
        var host = new Window
        {
            Content = view, Width = 1100, Height = 640, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
        };
        host.Show();
        try
        {
            await RenderLyricsView(view, "lyrics-wide", 1100, 640);
            var panel = (Border)view.FindName("LyricsPanel");
            var toggle = (Button)view.FindName("LyricsToggle");
            var items = (ItemsControl)view.FindName("LyricsItems");
            var scroll = (ScrollViewer)view.FindName("LyricsScroll");
            Check(panel.Visibility == Visibility.Visible && Grid.GetColumn(panel) == 1 && items.Items.Count == 7 &&
                toggle.Content?.ToString() == "Hide lyrics", "Wide Now Playing binds lyrics beside track details with a working toggle");
            var row = (ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(1);
            row.ApplyTemplate();
            var button = FindLyricsVisual<Button>(row)!;
            Check(button.Command is not null && button.Command.CanExecute(button.CommandParameter), "Lyric rows bind the seek command");
            var options = (Button)view.FindName("LyricsSettingsButton");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = Application.Current.Windows.OfType<LyricsSettingsWindow>().Single();
            settings.Left = settings.Top = -10000;
            settings.UpdateLayout();
            Check(settings.Owner == host && settings.DataContext == vm.Lyrics &&
                  ((DockPanel)view.FindName("LyricsHeader")).ActualHeight <= 36,
                "Lyrics options open in an owned window while the panel keeps a single compact header");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Application.Current.Windows.OfType<LyricsSettingsWindow>().Count() == 1,
                "Opening options again reuses the existing window");
            var size = (Slider)settings.FindName("LyricsFontSizeSlider");
            size.Value = 32;
            view.UpdateLayout();
            Check(vm.Lyrics.FontSize == 32 && button.FontSize == 32 && FindLyricsVisual<MusicPlayer.Controls.KaraokeLine>(button)!.FontSize == 32,
                "Font sizing in the options window updates the rendered karaoke text immediately");
            size.Value = 25;
            var earlier = (Button)settings.FindName("EarlierButton");
            earlier.Command.Execute(null);
            Check(vm.Lyrics.TimingAdjustmentSeconds == 0.1, "The options window binds the current track's timing controls");
            vm.Lyrics.ResetTimingCommand.Execute(null);
            Check(((Button)settings.FindName("ChooseLyricsFileButton")).Command.CanExecute(null),
                "The options window exposes sidecar selection for the playing track");
            settings.UpdateLayout();
            SaveLyricsPreview((FrameworkElement)settings.Content, "lyrics-options",
                (int)Math.Ceiling(((FrameworkElement)settings.Content).ActualWidth),
                (int)Math.Ceiling(((FrameworkElement)settings.Content).ActualHeight));
            settings.Close();
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Application.Current.Windows.OfType<LyricsSettingsWindow>().Single() != settings,
                "Closed options can be reopened");
            Application.Current.Windows.OfType<LyricsSettingsWindow>().Single().Close();
            button.Command!.Execute(button.CommandParameter);
            Check(vm.PositionSeconds == 5, "The rendered lyric button seeks to its line");
            vm.PositionSeconds = 30;
            await Task.Delay(350);
            view.UpdateLayout();
            Check(scroll.VerticalOffset > 0, "Automatic scrolling follows later lines");
            var beforeWheel = scroll.VerticalOffset;
            button.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            Check(scroll.VerticalOffset < beforeWheel, "Wheel-up over a lyric button moves the viewport after auto-follow animation");
            var afterWheelUp = scroll.VerticalOffset;
            panel.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            Check(scroll.VerticalOffset > afterWheelUp, "Wheel-down over the lyrics panel moves the viewport in the other direction");
            var resume = (Button)view.FindName("ResumeFollowingButton");
            Check(resume.Visibility == Visibility.Visible, "Manual scrolling offers Resume following");
            scroll.ScrollToTop();
            view.UpdateLayout();
            vm.PositionSeconds = 25;
            await Task.Delay(300);
            Check(scroll.VerticalOffset == 0, "Playback does not override manual scrolling while following is paused");
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Check(resume.Visibility == Visibility.Collapsed, "Resume following restores automatic scrolling");
            Check(scroll.VerticalOffset > 0, "Resuming returns to the currently active lyric");
            vm.PositionSeconds = 5;
            await RenderLyricsView(view, "lyrics-narrow", 520, 600);
            Check(Grid.GetColumn(panel) == 0 && Grid.GetRow(panel) == 1 && panel.ActualWidth > 450 && panel.ActualHeight > 250,
                "Narrow Now Playing gives lyrics full width below compact track details");
            toggle.Command.Execute(null);
            await RenderLyricsView(view, "lyrics-hidden", 1100, 640);
            Check(panel.Visibility == Visibility.Collapsed && toggle.Content?.ToString() == "Show lyrics",
                "The toggle hides the lyrics panel and restores the artwork layout");
            toggle.Command.Execute(null);
            await vm.Lyrics.LoadingTask;
            vm.CurrentTrack = Track("No sidecar");
            await vm.Lyrics.LoadingTask;
            await RenderLyricsView(view, "lyrics-missing", 520, 600);
            Check(((StackPanel)view.FindName("LyricsStatus")).Visibility == Visibility.Visible && items.Items.Count == 0,
                "Missing lyrics render a helpful state without leftover lines");
            vm.CurrentTrack = null;
            await RenderLyricsView(view, "lyrics-empty", 320, 480);
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings = Application.Current.Windows.OfType<LyricsSettingsWindow>().Single();
            host.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!settings.IsVisible, "Leaving Now Playing closes its options window");
        }
        finally { host.Close(); }
        Console.WriteLine("Now Playing lyrics render tests passed.");
    }

    private static T? FindLyricsVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) return result;
            if (FindLyricsVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static async Task RenderLyricsView(NowPlayingView view, string name, int width, int height)
    {
        view.Width = width;
        view.Height = height;
        if (view.Parent is Window host) { host.Width = width; host.Height = height; }
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        SaveLyricsPreview(view, name, width, height);
    }

    private static void SaveLyricsPreview(FrameworkElement element, string name, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "lyrics-previews");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}
