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

    private sealed class DeferredLyricsSource : ILocalLyricsSource
    {
        public List<DeferredLyricsRequest> Requests { get; } = [];
        public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null,
            string? lyricsFilePath = null, CancellationToken cancellationToken = default)
        {
            var request = new DeferredLyricsRequest(cancellationToken);
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed class DeferredLyricsRequest(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
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
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "lyrics-previews");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}
