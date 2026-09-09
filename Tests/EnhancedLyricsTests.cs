using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;
using NAudio.Wave;

internal static partial class Program
{
    private static async Task CheckEnhancedLyricsAsync()
    {
        const string text = "[offset:250]\n[00:01]<00:01>Hel<00:02>lo, <00:03>world!<00:04>\n" +
            "[00:05]Ordinary line\n[00:08]<00:08>First <00:09>second<00:12>\n[00:10]<00:10>Harmony<00:13>";
        using var lyrics = new LyricsViewModel(new FixedLyricsSource(text));
        lyrics.SetTrack(Track("Enhanced"), TimeSpan.FromSeconds(15));
        await lyrics.LoadingTask;
        var first = lyrics.Lines[0];
        lyrics.UpdatePosition(0.75);
        Check(first.IsActive && first.GetSegmentProgress(0) == 0, "A segment starts unfilled at its offset-adjusted onset");
        lyrics.UpdatePosition(1.25);
        Check(first.GetSegmentProgress(0) == 0.5 && first.GetSegmentProgress(1) == 0,
            "Enhanced LRC interpolates syllables continuously without starting the next word");
        lyrics.UpdatePosition(2.25);
        Check(first.GetSegmentProgress(0) == 1 && first.GetSegmentProgress(1) == 0.5,
            "Completed syllables stay filled while the next syllable progresses");
        lyrics.UpdatePosition(0.8);
        Check(first.GetSegmentProgress(0) < 0.1 && first.GetSegmentProgress(1) == 0,
            "Backward seeks immediately reset segment progress");
        lyrics.UpdatePosition(4.75);
        Check(!first.IsActive && lyrics.Lines[1].IsActive && lyrics.Lines[1].Line.Segments.Count == 0,
            "Ordinary LRC lines retain whole-line highlighting in a mixed document");
        lyrics.UpdatePosition(10.25);
        Check(lyrics.Lines[2].IsActive && lyrics.Lines[3].IsActive &&
            lyrics.Lines[2].GetSegmentProgress(1) == 0.5 && Math.Abs(lyrics.Lines[3].GetSegmentProgress(0) - 1d / 6) < 0.00001,
            "Overlapping vocals advance independently");
        var instantaneous = new LyricLineViewModel(new LyricLine("AB", TimeSpan.Zero, null, true,
            new[] { new LyricSegment("A", TimeSpan.Zero, TimeSpan.Zero, false), new LyricSegment("B", TimeSpan.Zero, null, true) }));
        Check(instantaneous.GetSegmentProgress(0) == 1 && instantaneous.GetSegmentProgress(1) == 1,
            "Zero-duration and unknown-end segments never divide by zero or invent a duration");

        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new PresentationTestPlayer(),
            lyricsSource: new FixedLyricsSource(text), monitorLibrary: false);
        vm.CurrentTrack = Track("Clock");
        await vm.Lyrics.LoadingTask;
        vm.RefreshLyricsPosition();
        Check(vm.Lyrics.Lines[0].GetSegmentProgress(0) == 0.5,
            "Lyrics use output presentation position rather than the decoder read-ahead position");

        await CheckEnhancedLyricsRenderingAsync(text);
        await CheckLyricsAudioClockAsync();
        Console.WriteLine("Enhanced lyrics tests passed.");
    }

    private sealed class FixedLyricsSource(string text) : ILocalLyricsSource
    {
        public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null, string? lyricsFilePath = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new LocalLyricsResult(LocalLyricsStatus.Loaded,
                null, new LrcParser().Parse(text, duration)));
    }

    private sealed class PresentationTestPlayer : IAudioPlayer
    {
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public TimeSpan Position => TimeSpan.FromSeconds(1.45);
        public TimeSpan PresentationPosition => TimeSpan.FromSeconds(1.25);
        public TimeSpan Duration => TimeSpan.FromSeconds(15);
        public float Volume { get; set; }
        public void Load(string path) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(TimeSpan position) { }
    }

    private static async Task CheckEnhancedLyricsRenderingAsync(string text)
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            lyricsSource: new FixedLyricsSource(text), monitorLibrary: false);
        vm.CurrentTrack = Track("Enhanced lyrics");
        vm.DurationSeconds = 180;
        await vm.Lyrics.LoadingTask;
        vm.PositionSeconds = 1.25;
        var view = new NowPlayingView { DataContext = vm, Background = (Brush)Application.Current.FindResource("CanvasBrush") };
        var host = new Window { Content = view, Width = 1100, Height = 640, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            await RenderLyricsView(view, "enhanced-wide", 1100, 640);
            var items = (ItemsControl)view.FindName("LyricsItems");
            var presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0);
            var control = FindLyricsVisual<KaraokeLine>(presenter)!;
            Check(control.Row == vm.Lyrics.Lines[0] && control.ActualHeight > 0, "Enhanced lyrics use the shaped text renderer inside the existing seek button");
            var before = RenderKaraokePixels(control);
            vm.PositionSeconds = 2.25;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var after = RenderKaraokePixels(control);
            Check(!before.SequenceEqual(after) && BrightPixelCount(after) > BrightPixelCount(before),
                "Advancing segment time visibly reveals more bright glyph pixels");
            vm.PositionSeconds = 1.25;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            Check(before.SequenceEqual(RenderKaraokePixels(control)), "Backward seek restores the exact earlier highlight image");

            vm.IsPlaying = true;
            player.Seek(TimeSpan.FromSeconds(1.6));
            await Task.Delay(100);
            Check(Math.Abs(control.Row!.LyricSeconds - 1.85) < 0.001, "Rendering frames refresh lyrics between the slider's quarter-second ticks");
            vm.PauseCommand.Execute(null);
            var frozen = control.Row.LyricSeconds;
            await Task.Delay(120);
            Check(control.Row.LyricSeconds == frozen, "Pausing freezes the enhanced highlight");

            await RenderLyricsView(view, "enhanced-narrow", 520, 600);
            // Exercise wrapping without splitting words/syllables into separate WPF text runs.
            var wrapped = new LyricLineViewModel(new LrcParser().Parse(
                "[00:01]<00:01>Hello, 世界 — a long lyric that wraps across several visual rows<00:05>").Lines[0])
                { IsActive = true, LyricSeconds = 3 };
            var standalone = new KaraokeLine { Row = wrapped, Width = 220, FontSize = 26, Foreground = Brushes.Gray,
                DimBrush = Brushes.Gray, HighlightBrush = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            host.Content = standalone;
            standalone.Measure(new Size(220, 1000));
            standalone.Arrange(new Rect(0, 0, 220, standalone.DesiredSize.Height));
            standalone.UpdateLayout();
            Check(standalone.ActualHeight > 60 && BrightPixelCount(RenderKaraokePixels(standalone)) > 0,
                "A timed segment can span wrapped lines and Unicode text without losing its fill");
            var wrappedBitmap = RenderKaraokeBitmap(standalone);
            Check(CountBrightRegion(wrappedBitmap, 0, wrappedBitmap.PixelWidth, 0, wrappedBitmap.PixelHeight / 4) > 0 &&
                CountBrightRegion(wrappedBitmap, 0, wrappedBitmap.PixelWidth, wrappedBitmap.PixelHeight * 3 / 4, wrappedBitmap.PixelHeight) == 0,
                "A half-filled wrapped segment finishes earlier rows before revealing the final row");
            SaveKaraokeImage(standalone, "enhanced-wrapped");
            var rtl = new LyricLineViewModel(new LrcParser().Parse("[00:01]<00:01>مرحبا بالعالم<00:05>").Lines[0])
                { IsActive = true, LyricSeconds = 3 };
            standalone.Row = rtl;
            standalone.FlowDirection = FlowDirection.RightToLeft;
            standalone.Measure(new Size(220, 1000));
            standalone.Arrange(new Rect(0, 0, 220, standalone.DesiredSize.Height));
            standalone.UpdateLayout();
            SaveKaraokeImage(standalone, "enhanced-rtl");
            Check(BrightPixelCount(RenderKaraokePixels(standalone)) > 0, "Right-to-left text retains shaped glyphs and a timed highlight");
            var rtlBitmap = RenderKaraokeBitmap(standalone);
            Check(CountBrightRegion(rtlBitmap, rtlBitmap.PixelWidth / 2, rtlBitmap.PixelWidth, 0, rtlBitmap.PixelHeight) >
                CountBrightRegion(rtlBitmap, 0, rtlBitmap.PixelWidth / 2, 0, rtlBitmap.PixelHeight),
                "Right-to-left segments reveal glyphs from the right edge rather than mirroring the sweep");
        }
        finally { host.Close(); }
    }

    private static RenderTargetBitmap RenderKaraokeBitmap(KaraokeLine control)
    {
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(control.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(control.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        // Render the control's local bounds, excluding its placement inside the host window.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(control) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, control.ActualWidth, control.ActualHeight) }, null,
                new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] RenderKaraokePixels(KaraokeLine control)
    {
        var bitmap = RenderKaraokeBitmap(control);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static int BrightPixelCount(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 190 && pixels[i + 3] > 200) count++;
        return count;
    }

    private static int CountBrightRegion(RenderTargetBitmap bitmap, int left, int right, int top, int bottom)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var count = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var index = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[index] > 190 && pixels[index + 3] > 200) count++;
        }
        return count;
    }

    private static void SaveKaraokeImage(KaraokeLine control, string name)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "lyrics-previews");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(RenderKaraokeBitmap(control)));
        encoder.Save(output);
    }

    private static async Task CheckLyricsAudioClockAsync()
    {
        if (WaveOut.DeviceCount == 0) { Console.WriteLine("SKIP: No output device for native presentation-clock checks."); return; }
        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsClockTests");
        var path = Path.Combine(temporary.Path, "silence.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1)))
            writer.Write(new byte[44100 * 2 * 2], 0, 44100 * 2 * 2);
        using var player = new NAudioPlayer { Volume = 0 };
        var ended = 0;
        player.PlaybackEnded += (_, _) => ended++;
        player.Load(path);
        player.Play();
        await Task.Delay(300);
        Check(player.PresentationPosition.TotalSeconds is > 0 and < 1 && player.Position >= player.PresentationPosition,
            "Native presentation position follows rendered audio rather than buffered decoder reads");
        player.Pause();
        var paused = player.PresentationPosition;
        await Task.Delay(150);
        Check(player.PresentationPosition == paused, "The native output clock freezes while paused");
        player.Seek(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Check(Math.Abs(player.PresentationPosition.TotalSeconds - 1) < 0.001 && ended == 0,
            "Paused seeking resets the device clock and ignores stopped events from the replaced output");
        player.Play();
        await Task.Delay(150);
        Check(player.PresentationPosition.TotalSeconds is > 1 and < 1.5, "Resume advances from the seek origin");
        player.Seek(TimeSpan.FromSeconds(0.2));
        await Task.Delay(150);
        Check(player.PresentationPosition.TotalSeconds is >= 0.2 and < 0.8 && ended == 0,
            "Playing backward seeks discard queued audio and reset the presentation clock");
        player.Stop();
        Check(player.PresentationPosition == TimeSpan.Zero, "Stop resets the native presentation clock");
        player.Seek(TimeSpan.FromSeconds(1.8));
        player.Play();
        await Task.Delay(600);
        Check(ended == 1 && player.PresentationPosition == player.Duration, "Natural completion reports duration and ends exactly once");
    }
}
