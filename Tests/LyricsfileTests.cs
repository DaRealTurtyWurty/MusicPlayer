using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private const string LyricsfileHeader = "version: '1.0'\nmetadata: {title: Two voices, artist: Maya & Alex}\n";
    private const string LyricsfileFixture = LyricsfileHeader + """
        lines:
          - text: 'Stay with me'
            start_ms: 2000
            end_ms: 8000
            words:
              - {text: 'Stay ', start_ms: 2000, end_ms: 4000}
              - {text: 'with ', start_ms: 4000, end_ms: 6000}
              - {text: 'me', start_ms: 6000, end_ms: 8000}
          - text: 'Through the night'
            start_ms: 4000
            end_ms: 10000
            words:
              - {text: 'Through ', start_ms: 4000, end_ms: 6000}
              - {text: 'the night', start_ms: 6000, end_ms: 10000}
        plain: |
          [Chorus]
          Stay with me
          Through the night
        """;

    private static async Task CheckLyricsfileAsync()
    {
        var parser = new LyricsfileParser();
        var doc = parser.Parse(LyricsfileFixture);
        Check(doc.Diagnostics.Count == 0 && doc.Metadata["title"] == "Two voices" && doc.Lines.Count == 2 &&
            doc.TimingMode == LyricsTimingMode.Segment && doc.PlainText!.StartsWith("[Chorus]\n"),
            "Lyricsfile reads block/flow YAML, metadata and a distinct plain version");
        Check(doc.Lines[0].End == TimeSpan.FromSeconds(8) && doc.Lines[1].Start == TimeSpan.FromSeconds(4) &&
            doc.Lines[0].Segments[1].Start == TimeSpan.FromSeconds(4) && !doc.Lines[0].IsEndInferred,
            "Lyricsfile uses absolute word milliseconds and preserves explicit overlapping line intervals");
        var unicode = parser.Parse(LyricsfileHeader + "lines:\n- text: '  Hello, 世界!  '\n  start_ms: 1000\n  words:\n" +
            "  - {text: '  Hel', start_ms: 1000}\n  - {text: 'lo, ', start_ms: 1500}\n  - {text: '世界!  ', start_ms: 2000, end_ms: 3000}\n");
        Check(unicode.Lines[0].Text == "  Hello, 世界!  " && string.Concat(unicode.Lines[0].Segments.Select(word => word.Text)) == unicode.Lines[0].Text &&
            unicode.Lines[0].Segments[0].End == TimeSpan.FromMilliseconds(1500) && unicode.Lines[0].Segments[0].IsEndInferred,
            "Lyricsfile preserves syllables, Unicode and whitespace while inferring missing word ends");
        var inferred = parser.Parse(LyricsfileHeader + "lines:\n- {text: Lead, start_ms: 1000}\n- {text: Other, start_ms: 2000}\n", TimeSpan.FromSeconds(20));
        Check(inferred.Lines[0].End == TimeSpan.FromSeconds(6) && inferred.Lines[0].IsEndInferred &&
            parser.Parse(LyricsfileHeader + "lines: [{text: Last, start_ms: 9000}]", TimeSpan.FromSeconds(10)).Lines[0].End == TimeSpan.FromSeconds(10),
            "Unknown Lyricsfile line ends use a duration-capped five-second display window, independently of other singers");
        var wordOverlap = parser.Parse(LyricsfileHeader + "lines:\n- text: AB\n  start_ms: 1000\n  words:\n" +
            "  - {text: A, start_ms: 1000, end_ms: 12000}\n  - {text: B, start_ms: 2000}\n");
        Check(wordOverlap.Lines[0].End == TimeSpan.FromSeconds(12) && wordOverlap.Lines[0].Segments[0].End == TimeSpan.FromSeconds(12),
            "Missing ends never truncate an explicitly sustained overlapping word");
        var ordering = parser.Parse(LyricsfileHeader + "lines:\n- {text: Later, start_ms: 2000}\n- {text: First, start_ms: 1000}\n- {text: Harmony, start_ms: 1000}\n");
        Check(ordering.Lines.Select(line => line.Text).SequenceEqual(new[] { "First", "Harmony", "Later" }), "Lyricsfile sorts lines stably, including simultaneous starts");
        var missingLineEnd = parser.Parse(LyricsfileHeader + "lines:\n- text: AB\n  start_ms: 1000\n  words:\n" +
            "  - {text: A, start_ms: 1000, end_ms: 5000}\n  - {text: B, start_ms: 2000, end_ms: 3000}\n");
        Check(missingLineEnd.Lines[0].End == TimeSpan.FromSeconds(5), "Fully timed words supply a missing line end using the latest end, not the last word");
        var mismatched = parser.Parse(LyricsfileHeader + "lines: [{text: Fallback, start_ms: 1000, end_ms: 3000, words: [{text: Actual, start_ms: 1000, end_ms: 2000}]}]");
        Check(mismatched.Lines[0].Text == "Actual" && mismatched.Diagnostics.Count == 1,
            "Timed word text controls highlighting when a stale fallback line differs");
        var badWord = parser.Parse(LyricsfileHeader + "lines: [{text: Fallback, start_ms: 1000, end_ms: 3000, words: [{text: Bad, start_ms: 2000, end_ms: 1500}]}]");
        Check(badWord.Lines[0].Text == "Fallback" && badWord.Lines[0].Segments.Count == 0 && badWord.Diagnostics.Count == 1,
            "Invalid word intervals fall back to usable line timing");
        var badLine = parser.Parse(LyricsfileHeader + "lines:\n- {text: Bad, start_ms: -1}\n- {text: Good, start_ms: 1000}\n");
        Check(badLine.Lines.Count == 1 && badLine.Diagnostics[0].LineNumber == 4, "Invalid lines are skipped with accurate source-line diagnostics");
        var plain = parser.Parse(LyricsfileHeader + "plain: |\n  [Verse]\n\n  Just words.\n");
        Check(plain.HasLyrics && plain.TimingMode == LyricsTimingMode.Plain && plain.Lines.Count == 0 && plain.PlainText == "[Verse]\n\nJust words.\n",
            "Plain Lyricsfiles preserve section labels and blank lines without inventing timestamps");
        Check(parser.Parse(LyricsfileHeader + "plain: \"Just words.\\nMore words.\"").PlainText == "Just words.\nMore words.",
            "Plain lyrics accept quoted multiline YAML as well as literal blocks");
        var instrumental = parser.Parse("version: '1.0'\nmetadata: {title: Prelude, artist: Maya, instrumental: true}\nlines: []\n");
        Check(instrumental.IsInstrumental && !instrumental.HasLyrics && instrumental.Diagnostics.Count == 0,
            "Instrumental Lyricsfiles are valid without sung text");
        var offset = parser.Parse("version: '1.0'\nmetadata: {title: Song, artist: Maya, offset_ms: 250}\nlines: [{text: Start, start_ms: 1000}]\n");
        Check(offset.Offset == TimeSpan.Zero && offset.Metadata["offset_ms"] == "250" && offset.Diagnostics.Count == 1,
            "Undefined Lyricsfile offsets are retained with a warning instead of guessed");
        foreach (var (invalid, description) in new[]
        {
            (LyricsfileFixture.Replace("'1.0'", "'2.0'"), "unknown versions"),
            (LyricsfileFixture.Replace("'1.0'", "1.0"), "numeric versions"),
            (LyricsfileFixture + "\nversion: '1.0'\n", "duplicate keys"),
            (LyricsfileFixture + "\n---\nplain: extra\n", "multiple documents"),
            (LyricsfileHeader + "plain: !Custom words", "custom tags"),
            (LyricsfileHeader + "plain: &a words\nextra: *a", "anchors and aliases"),
            (LyricsfileHeader + "extra: " + new string('[', 70) + "x" + new string(']', 70), "excessive nesting"),
            (LyricsfileHeader + "plain: \"unterminated", "malformed YAML"),
            ("version: '1.0'\nmetadata: {title: Song}\nplain: words", "missing required metadata"),
            ("version: '1.0'\nmetadata: {title: Song, artist: Maya, instrumental: true}\nplain: words", "conflicting instrumental text"),
            (LyricsfileHeader + "lines: [{text: Word, start_ms: '1000'}]", "string timestamps"),
            (LyricsfileHeader + "lines: [{text: Word, start_ms: 1.5}]", "fractional timestamps"),
            (LyricsfileHeader + "lines: [{text: Word, start_ms: 9223372036854775807}]", "overflowing timestamps")
        })
        {
            var result = parser.Parse(invalid);
            Check(!result.HasLyrics && !result.IsInstrumental && result.Diagnostics.Count > 0, $"Lyricsfile rejects {description}");
        }
        Check(!parser.Parse(new string(' ', 4 * 1024 * 1024 + 1)).HasLyrics, "Lyricsfile bounds document size");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(parser.Parse(LyricsfileFixture).Lines[0].Start.TotalMilliseconds == 2000, "Lyricsfile timing is culture independent");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { parser.Parse(LyricsfileFixture, cancellationToken: cancelled.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }

        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsfileTests");
        var audio = Path.Combine(temporary.Path, "01. Two voices.flac");
        var yamlPath = Path.ChangeExtension(audio, ".LYRICSFILE.YAML");
        var ttmlPath = Path.ChangeExtension(audio, ".ttml");
        var lrcPath = Path.ChangeExtension(audio, ".lrc");
        var source = new LocalLyricsSource();
        await File.WriteAllTextAsync(lrcPath, "[00:01]LRC fallback");
        await File.WriteAllTextAsync(yamlPath, LyricsfileFixture);
        Check((await source.LoadAsync(audio)).Document!.Metadata["format"] == "lyricsfile", "Lyricsfile discovery handles uppercase compound extensions and precedes LRC");
        await File.WriteAllTextAsync(ttmlPath, VocalTtml);
        Check((await source.LoadAsync(audio)).Document!.Metadata["format"] == "ttml", "TTML retains discovery priority over Lyricsfile");
        Check((await source.LoadAsync(audio, lyricsFilePath: yamlPath)).Document!.Metadata["format"] == "lyricsfile", "Explicit Lyricsfile selection overrides other sidecars");
        Check((await source.LoadAsync(audio, lyricsFilePath: lrcPath)).Document!.Lines[0].Text == "LRC fallback", "Explicit LRC selection still overrides richer formats");
        var onlyYamlAudio = Path.Combine(temporary.Path, "Other.flac");
        await File.WriteAllTextAsync(Path.ChangeExtension(onlyYamlAudio, ".lyricsfile.yaml"), "version: [");
        await File.WriteAllTextAsync(Path.ChangeExtension(onlyYamlAudio, ".lrc"), "[00:01]Fallback");
        Check((await source.LoadAsync(onlyYamlAudio)).Status == LocalLyricsStatus.Invalid, "Invalid preferred Lyricsfiles cannot silently fall back to LRC");
        Check((await source.LoadAsync(audio, lyricsFilePath: Path.Combine(temporary.Path, "Missing.lyricsfile.yaml"))).Status == LocalLyricsStatus.NotFound,
            "An explicit missing Lyricsfile never selects a different sidecar");
        Console.WriteLine("Lyricsfile parsing and discovery tests passed.");
    }

    private static async Task CheckLyricsfileViewAsync()
    {
        await CheckLyricsfileAsync();
        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsfileViewTests");
        var audio = Path.Combine(temporary.Path, "Two voices.flac");
        var sidecar = Path.ChangeExtension(audio, ".lyricsfile.yaml");
        await File.WriteAllTextAsync(sidecar, LyricsfileFixture);
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, monitorLibrary: false);
        vm.CurrentTrack = new Track { FilePath = audio, Title = "Two voices", Artist = "Maya & Alex", Duration = TimeSpan.FromSeconds(40) };
        vm.DurationSeconds = 40;
        await vm.Lyrics.LoadingTask;
        vm.PositionSeconds = 5;
        Check(vm.Lyrics.ActiveLines.Count == 2 && vm.Lyrics.ActiveLine == vm.Lyrics.Lines[0] &&
            vm.Lyrics.Lines[0].GetSegmentProgress(1) == 0.5 && vm.Lyrics.Lines[1].GetSegmentProgress(0) == 0.5,
            "Discovered Lyricsfile words and overlapping lines drive the playback synchronizer");
        vm.Lyrics.SeekToLineCommand.Execute(vm.Lyrics.Lines[1]);
        Check(vm.PositionSeconds == 4 && !vm.IsPlaying, "Lyricsfile clicks seek without starting paused playback");
        var view = new NowPlayingView { DataContext = vm, Background = (Brush)Application.Current.FindResource("CanvasBrush") };
        var host = new Window { Content = view, Width = 1000, Height = 740, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            vm.PositionSeconds = 5;
            await RenderLyricsView(view, "lyricsfile-words", 1000, 740);
            var items = (ItemsControl)view.FindName("LyricsItems");
            var wordControl = FindLyricsVisual<KaraokeLine>((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0))!;
            Check(wordControl.Row!.IsActive && wordControl.Row.Line.Segments.Count == 3, "Now Playing renders Lyricsfile word segments");
            await File.WriteAllTextAsync(sidecar, LyricsfileHeader + "plain: |\n  [Verse]\n\n  Just words.\n  A quiet ending.\n");
            vm.Lyrics.ReloadCommand.Execute(null);
            await vm.Lyrics.LoadingTask;
            vm.PositionSeconds = 7;
            var beforeSeek = vm.PositionSeconds;
            vm.Lyrics.SeekToLineCommand.Execute(vm.Lyrics.Lines[0]);
            Check(vm.Lyrics.HasLyrics && vm.Lyrics.ActiveLines.Count == 0 && vm.Lyrics.ActiveLine is null &&
                vm.Lyrics.Lines[1].Text == "" && vm.PositionSeconds == beforeSeek, "Plain Lyricsfiles display without cue placeholders, false highlights or seeking");
            await RenderLyricsView(view, "lyricsfile-plain", 520, 680);
            var button = FindLyricsVisual<Button>((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0))!;
            Check(!button.IsEnabled && button.ToolTip is null, "Plain lyric rows expose no misleading seek action");
            await File.WriteAllTextAsync(sidecar, "version: '1.0'\nmetadata: {title: Prelude, artist: Maya, instrumental: true}\n");
            vm.Lyrics.ReloadCommand.Execute(null);
            await vm.Lyrics.LoadingTask;
            Check(!vm.Lyrics.HasLyrics && vm.Lyrics.StatusTitle == "Instrumental", "Instrumental files display a meaningful empty state");
        }
        finally { host.Close(); }
        Console.WriteLine("Lyricsfile Now Playing tests passed.");
    }
}
