using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private const string TtmlOpen = "<tt xmlns='http://www.w3.org/ns/ttml' xmlns:ttp='http://www.w3.org/ns/ttml#parameter' xmlns:ttm='http://www.w3.org/ns/ttml#metadata'";
    private const string VocalTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"
            xmlns:itunes="http://itunes.apple.com/lyric-ttml-extensions" xml:lang="en">
          <head><metadata>
            <ttm:title>Two voices</ttm:title>
            <ttm:agent xml:id="v1" type="person"><ttm:name type="full">Maya</ttm:name></ttm:agent>
            <ttm:agent xml:id="v2" type="person"><ttm:name type="full">Alex</ttm:name></ttm:agent>
          </metadata></head>
          <body dur="00:40.000"><div begin="00:02.000" end="00:38.000">
            <p begin="00:02.000" end="00:08.000" ttm:agent="v1"><span begin="00:02.000" end="00:04.000">Stay </span><span begin="00:04.000" end="00:08.000">with me</span><span ttm:role="x-bg"><span begin="00:03.000" end="00:05.000">(Stay </span><span begin="00:05.000" end="00:07.000">with me)</span></span></p>
            <p begin="00:04.000" end="00:10.000" ttm:agent="v2"><span begin="00:04.000" end="00:06.000">Through </span><span begin="00:06.000" end="00:10.000">the night</span></p>
            <p begin="00:12.000" end="00:18.000" ttm:agent="v1">Every light along the river</p>
            <p begin="00:16.000" end="00:22.000" ttm:agent="v2">Brings us closer to the shore</p>
            <p begin="00:24.000" end="00:30.000" ttm:agent="v1">Let the morning find us here</p>
            <p begin="00:28.000" end="00:36.000" ttm:agent="v2">We have everything we need</p>
          </div></body>
        </tt>
        """;

    private static async Task CheckTtmlAsync()
    {
        var parser = new TtmlParser();
        var standard = parser.Parse(TtmlOpen + "><body begin='10s' dur='20s'><div begin='2s'><p begin='1s' dur='5s'>" +
            "<span begin='0s' dur='2s'>Hel</span><span begin='2s' dur='2s'>lo!</span></p></div></body></tt>");
        Check(standard.HasLyrics && standard.Lines[0].Text == "Hello!" && standard.Lines[0].Start.TotalSeconds == 13 &&
            standard.Lines[0].End?.TotalSeconds == 17 && standard.Lines[0].Segments[1].Start.TotalSeconds == 15,
            "Standard TTML resolves nested relative starts and durations without splitting syllable text");
        Check(standard.Diagnostics.Count == 0 && standard.Metadata["timingConvention"] == "ttml-relative", "Valid standard TTML has no import warnings");
        var clocks = parser.Parse(TtmlOpen + "><body><div begin='00:00:10'><p begin='00:00:02' end='00:00:04'>Relative</p></div></body></tt>");
        Check(clocks.Lines[0].Start.TotalSeconds == 12 && clocks.Lines[0].End?.TotalSeconds == 14,
            "Standard clock expressions are relative to their parent container");
        var apple = parser.Parse(VocalTtml);
        Check(apple.Lines.Count == 7 && apple.Lines[0].Start.TotalSeconds == 2 && apple.Lines[0].End?.TotalSeconds == 8 &&
            apple.Lines[1].IsBackground && apple.Lines[1].Start.TotalSeconds == 3 && apple.Lines[1].End?.TotalSeconds == 7,
            "Apple lyric clocks remain absolute and nested backing vocals become independently timed lines");
        Check(apple.Lines[0].Text == "Stay with me" && apple.Lines[1].Text == "(Stay with me)" &&
            apple.Lines[0].VocalGroupId == apple.Lines[1].VocalGroupId && apple.Lines[0].VocalistName == "Maya" &&
            apple.Lines[2].VocalistName == "Alex" && apple.Metadata["title"] == "Two voices",
            "Vocal separation preserves group membership, singer names and metadata without duplicating backing text");
        Check(apple.Diagnostics.Count == 0, "Apple duet and backing-vocal import has no warnings");
        var shorthand = parser.Parse(TtmlOpen + "><body><div begin='00:10'><p begin='00:12' end='00:14'>Absolute</p></div></body></tt>");
        Check(shorthand.Lines[0].Start.TotalSeconds == 12, "Two-field lyric clock shorthand identifies Apple's absolute timing convention");

        var sequential = parser.Parse(TtmlOpen + "><body><div begin='10s' timeContainer='seq'>" +
            "<p dur='2s'>First</p><p begin='1s' dur='3s'>Second</p></div></body></tt>");
        Check(sequential.Lines[0].Start.TotalSeconds == 10 && sequential.Lines[1].Start.TotalSeconds == 13 &&
            sequential.Lines[1].End?.TotalSeconds == 16, "Sequential containers anchor each item to the preceding item's end");
        var frame = parser.Parse(TtmlOpen + " ttp:frameRate='30' ttp:frameRateMultiplier='1000 1001' ttp:tickRate='1000' ttp:subFrameRate='2'>" +
            "<body><div><p begin='30f' dur='500t'>Frames</p><p begin='00:00:02:15.1' dur='1s'>Subframe</p></div></body></tt>");
        Check(Math.Abs(frame.Lines[0].Start.TotalSeconds - 1.001) < 0.00001 &&
            Math.Abs(frame.Lines[0].End!.Value.TotalSeconds - 1.501) < 0.00001 &&
            Math.Abs(frame.Lines[1].Start.TotalSeconds - (2 + 15.5 / (30 * 1000d / 1001))) < 0.00001,
            "Frame, subframe and tick times respect the document's timing rates");
        var whitespace = parser.Parse(TtmlOpen + "><body><p begin='1s' end='5s'>  <span begin='0s' end='1s'>Hello</span> \n " +
            "<span begin='1s' end='2s'>&amp; 世界</span><br/><span begin='2s' end='3s'>again</span>  </p>" +
            "<p begin='6s' end='8s' xml:space='preserve'>  Keep   spaces  </p></body></tt>");
        Check(whitespace.Lines[0].Text == "Hello & 世界\nagain" && whitespace.Lines[1].Text == "  Keep   spaces  " &&
            string.Concat(whitespace.Lines[0].Segments.Select(segment => segment.Text)) == whitespace.Lines[0].Text,
            "Whitespace normalization, explicit line breaks, Unicode and xml:space preserve text-to-segment mapping");
        var clipping = parser.Parse(TtmlOpen + "><body dur='5s'><p begin='3s' end='9s'>Clipped</p><p begin='6s' end='7s'>Outside</p></body></tt>");
        Check(clipping.Lines.Count == 1 && clipping.Lines[0].End?.TotalSeconds == 5 && clipping.Diagnostics.Count > 0,
            "Ancestor intervals clip child timing and exclude vocals outside the body");
        var nestedClipping = parser.Parse(TtmlOpen + "><body begin='00:10' end='00:20'><div begin='00:01' end='00:15'>" +
            "<p begin='00:02' end='00:14'><span begin='00:03' end='00:13'>Clipped</span></p></div>" +
            "<div begin='00:12' end='00:12'><p begin='00:11' end='00:13'>Empty parent</p></div></body></tt>");
        Check(nestedClipping.Lines.Count == 1 && nestedClipping.Lines[0].Start.TotalSeconds == 10 &&
            nestedClipping.Lines[0].Segments[0].Start.TotalSeconds == 10,
            "Nested Apple clocks inherit ancestor start clipping and empty parents cannot leak paragraphs");
        var spanVoices = parser.Parse(TtmlOpen + "><head><metadata><ttm:agent xml:id='a'><ttm:name>Ada</ttm:name></ttm:agent></metadata></head>" +
            "<body ttm:agent='a'><p begin='1s' end='5s'><span begin='0s' dur='3s'>Main</span>" +
            "<span begin='1s' dur='2s' ttm:agent='b'>Other</span><span ttm:role='x-translation'>Translation</span></p></body></tt>");
        Check(spanVoices.Lines.Count == 2 && spanVoices.Lines[0].VocalistName == "Ada" && spanVoices.Lines[1].VocalistId == "b" &&
            !spanVoices.Lines.Any(line => line.Text.Contains("Translation")), "Agent inheritance and inline voice changes remain separate from translation metadata");
        var invalid = parser.Parse(TtmlOpen + "><body><p begin='nonsense' end='4s'>Bad</p><p begin='5s' end='7s'>Good</p></body></tt>");
        Check(invalid.Lines.Count == 1 && invalid.Lines[0].Text == "Good" && invalid.Diagnostics.Count == 1,
            "Invalid TTML timing reports diagnostics while retaining valid paragraphs");
        Check(!parser.Parse("<tt/>").HasLyrics && !parser.Parse(TtmlOpen + " ttp:timeBase='clock'><body><p begin='1s'>Unsupported</p></body></tt>").HasLyrics,
            "Wrong namespaces and unsupported timing bases cannot silently produce inaccurate lyrics");
        Check(!parser.Parse("<!DOCTYPE tt [<!ENTITY secret SYSTEM 'file:///private.txt'>]>" + TtmlOpen + "><body><p begin='1s' end='2s'>&secret;</p></body></tt>").HasLyrics,
            "TTML parsing prohibits DTDs and external entity resolution");
        Check(!parser.Parse(TtmlOpen + "><body><p>Untimed</p></body></tt>", TimeSpan.FromSeconds(10)).HasLyrics,
            "Audio duration alone does not turn untimed TTML text into synchronized lyrics");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { parser.Parse(VocalTtml, cancellationToken: cancelled.Token); throw new InvalidOperationException("Expected TTML cancellation."); }
        catch (OperationCanceledException) { }

        using var temporary = new TemporaryTestDirectory("MusicPlayerTtmlTests");
        var audio = Path.Combine(temporary.Path, "Duet.flac");
        var ttmlPath = Path.Combine(temporary.Path, "Duet.TTML");
        var lrcPath = Path.ChangeExtension(audio, ".lrc");
        await File.WriteAllTextAsync(lrcPath, "[00:01]LRC fallback");
        var source = new LocalLyricsSource();
        Check((await source.LoadAsync(audio)).Document!.Lines[0].Text == "LRC fallback", "Absent TTML falls back to an existing LRC sidecar");
        await File.WriteAllTextAsync(ttmlPath, VocalTtml);
        Check((await source.LoadAsync(audio)).Document!.Lines.Count == 7, "Same-basename TTML takes priority over LRC, including uppercase extensions");
        Check((await source.LoadAsync(audio, lyricsFilePath: lrcPath)).Document!.Lines[0].Text == "LRC fallback", "Explicit LRC selection overrides automatic TTML priority");
        await File.WriteAllTextAsync(ttmlPath, "<broken");
        Check((await source.LoadAsync(audio)).Status == LocalLyricsStatus.Invalid, "Malformed preferred TTML is reported rather than silently replaced with LRC");
        Check((await source.LoadAsync(audio, lyricsFilePath: Path.Combine(temporary.Path, "Missing.ttml"))).Status == LocalLyricsStatus.NotFound,
            "An explicit missing TTML never falls back to a different file");
        Console.WriteLine("TTML parsing and discovery tests passed.");
    }

    private static async Task CheckTtmlVocalsAsync()
    {
        await CheckTtmlAsync();
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            lyricsSource: new TtmlFixtureSource(), monitorLibrary: false);
        vm.CurrentTrack = new Track { FilePath = "duet.flac", Title = "Two voices", Artist = "Maya & Alex", Album = "After the Rain", Duration = TimeSpan.FromSeconds(40) };
        vm.DurationSeconds = 40;
        await vm.Lyrics.LoadingTask;
        vm.PositionSeconds = 5;
        Check(vm.Lyrics.ActiveLines.Count == 3 && vm.Lyrics.ActiveLine == vm.Lyrics.Lines[0],
            "Lead, backing and duet parts highlight simultaneously while following stays on the lead");
        Check(vm.Lyrics.Lines[1].IsBackground && vm.Lyrics.Lines[1].VocalLabel == "Maya · Backing vocals" && vm.Lyrics.Lines[2].IsSecondaryVocal,
            "Backing vocals and named singers receive distinct presentation roles");
        Check(vm.Lyrics.Lines[0].GetSegmentProgress(1) == 0.25 && vm.Lyrics.Lines[2].GetSegmentProgress(0) == 0.5,
            "Overlapping TTML vocals fill according to their own segment intervals");
        vm.PositionSeconds = 8.5;
        Check(vm.Lyrics.ActiveLines.Count == 1 && vm.Lyrics.ActiveLine == vm.Lyrics.Lines[2], "Following moves to the remaining singer after the lead and backing parts end");
        vm.PositionSeconds = 3.5;
        Check(vm.Lyrics.ActiveLines.Count == 2 && vm.Lyrics.ActiveLine == vm.Lyrics.Lines[0], "Backward seeks restore the earlier overlapping vocal set");
        vm.Lyrics.SeekToLineCommand.Execute(vm.Lyrics.Lines[1]);
        Check(vm.PositionSeconds == 3 && !vm.IsPlaying, "Backing-vocal clicks seek to their own onset without starting paused playback");
        vm.PositionSeconds = 5;
        var view = new NowPlayingView { DataContext = vm, Background = (Brush)Application.Current.FindResource("CanvasBrush") };
        var host = new Window { Content = view, Width = 1100, Height = 740, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            await RenderLyricsView(view, "ttml-duet-wide", 1100, 740);
            await Task.Delay(300);
            view.UpdateLayout();
            var items = (ItemsControl)view.FindName("LyricsItems");
            var lead = FindLyricsVisual<KaraokeLine>((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0))!;
            var backing = FindLyricsVisual<KaraokeLine>((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(1))!;
            var second = FindLyricsVisual<KaraokeLine>((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(2))!;
            Check(lead.Row!.IsActive && backing.Row!.IsActive && second.Row!.IsActive && backing.FontSize < lead.FontSize &&
                second.TextAlignment == TextAlignment.Right, "Rendered vocals remain separate, with smaller backing text and an opposing duet alignment");
            var scroll = (ScrollViewer)view.FindName("LyricsScroll");
            var top = lead.TranslatePoint(new Point(), scroll).Y;
            var bottom = second.TranslatePoint(new Point(0, second.ActualHeight), scroll).Y;
            Check(top >= 0 && bottom <= scroll.ViewportHeight, "Auto-follow keeps all fitting overlapping vocal parts visible");
            await RenderLyricsView(view, "ttml-duet-narrow", 520, 680);
            vm.Lyrics.IsEnabled = false;
            Check(vm.Lyrics.ActiveLines.Count == 0, "Hiding lyrics clears the overlapping vocal set");
        }
        finally { host.Close(); }
        Console.WriteLine("TTML overlapping-vocal rendering tests passed.");
    }

    private sealed class TtmlFixtureSource : ILocalLyricsSource
    {
        public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null, string? lyricsFilePath = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new LocalLyricsResult(LocalLyricsStatus.Loaded, null,
                new TtmlParser().Parse(VocalTtml, duration, cancellationToken)));
    }
}
