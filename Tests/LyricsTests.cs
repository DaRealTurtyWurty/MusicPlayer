using System.Globalization;
using System.IO;
using System.Text;
using MusicPlayer.Models;
using MusicPlayer.Services;

internal static partial class Program
{
    private static async Task CheckLyricsAsync()
    {
        var parser = new LrcParser();
        var document = parser.Parse("\uFEFF[ar:Artist]\r\n[ti:Song: subtitle]\r\n[custom:kept]\r\n" +
            "[00:20.125]Second\r\n[00:05.5][00:15.50]  First!  \r\n[00:10]\r\n[00:15.500]Harmony\r\n[offset:+250]",
            TimeSpan.FromSeconds(30));
        Check(document.Metadata["AR"] == "Artist" && document.Metadata["ti"] == "Song: subtitle" &&
            document.Metadata["custom"] == "kept", "LRC metadata is preserved case-insensitively, including unknown tags");
        Check(document.Offset == TimeSpan.FromMilliseconds(250) && document.Lines[0].Start == TimeSpan.FromMilliseconds(5500),
            "LRC offset is retained separately, without modifying source timestamps");
        Check(document.Lines.Select(line => line.Start.TotalMilliseconds).SequenceEqual(new double[] { 5500, 10000, 15500, 15500, 20125 }),
            "LRC sorts lines stably, expands chorus timestamps and supports seconds, tenths, hundredths and milliseconds");
        Check(document.Lines[0].Text == "  First!  " && document.Lines[1].Text == "" && document.Lines[3].Text == "Harmony",
            "LRC preserves whitespace, instrumental cues and simultaneous lyrics");
        Check(document.Lines[2].End == document.Lines[3].End && document.Lines[2].End == TimeSpan.FromMilliseconds(20125) &&
            document.Lines[^1].End == TimeSpan.FromSeconds(30) && document.Lines.All(line => line.IsEndInferred),
            "Line ends use the next distinct start and final track duration");
        Check(document.Diagnostics.Count == 0 && document.TimingMode == LyricsTimingMode.Line, "Valid line LRC has no warnings");
        Check(parser.Parse("[00:05]Last").Lines[0].End is null &&
            parser.Parse("[00:05]Last", TimeSpan.FromSeconds(3)).Lines[0].End is null,
            "Unknown or shorter duration does not fabricate an invalid final end");

        var enhanced = parser.Parse("[00:10]<00:10>Hel<00:10.5>lo, <00:11>世界!<00:12>\n[00:15]Outro",
            TimeSpan.FromSeconds(20));
        var line = enhanced.Lines[0];
        Check(enhanced.TimingMode == LyricsTimingMode.Segment && line.Text == "Hello, 世界!" &&
            line.Segments.Select(segment => segment.Text).SequenceEqual(new[] { "Hel", "lo, ", "世界!" }),
            "Enhanced LRC preserves syllables, punctuation, Unicode and spaces");
        Check(line.End == TimeSpan.FromSeconds(12) && !line.IsEndInferred &&
            line.Segments[0].End == TimeSpan.FromSeconds(10.5) && line.Segments[0].IsEndInferred &&
            line.Segments[^1].End == TimeSpan.FromSeconds(12) && !line.Segments[^1].IsEndInferred,
            "Enhanced LRC distinguishes next-segment estimates from explicit terminal ends");
        var repeated = parser.Parse("[00:10][00:30]Hello <00:11>world<00:12>");
        Check(repeated.Lines[1].Segments[0].Start == TimeSpan.FromSeconds(30) &&
            repeated.Lines[1].Segments[1].Start == TimeSpan.FromSeconds(31) && repeated.Lines[1].End == TimeSpan.FromSeconds(32),
            "Repeated enhanced lines shift inline timestamps and retain the prefix text");
        var inferred = parser.Parse("[00:01]<00:01>One <00:02>two\n[00:04]Next");
        Check(inferred.Lines[0].Segments[^1].End == TimeSpan.FromSeconds(4) && inferred.Lines[0].Segments[^1].IsEndInferred,
            "The final word inherits an inferred line end when no terminal tag exists");
        var overlap = parser.Parse("[00:01]<00:01>Lead<00:06>\n[00:03]<00:03>Backing<00:04>");
        Check(overlap.Lines[0].End == TimeSpan.FromSeconds(6) && overlap.Lines[0].Segments.Count == 1,
            "Explicit ends preserve overlapping vocals");

        var malformed = parser.Parse("[offset:nope]\n[00:61]Bad\n[999999999999999999999999:00]Huge\nplain text\n" +
            "[00:02][00:bad]Bad repeated\n[00:05]<00:06>A<00:05>B\n[00:08]<00:xx>C\n[00:09]Good");
        Check(malformed.Lines.Count == 3 && malformed.Diagnostics.Count == 7 && malformed.Lines[0].Text == "AB" &&
            malformed.Lines[0].Segments.Count == 0 && malformed.Lines[^1].Text == "Good",
            "Malformed input reports source diagnostics and preserves usable line lyrics");
        Check(malformed.Diagnostics[0].LineNumber == 1 && malformed.Diagnostics[^1].LineNumber == 7,
            "Diagnostics identify one-based source lines");
        Check(!parser.Parse("[ar:Artist]\n[00:02]\n").HasLyrics && !parser.Parse("").HasLyrics,
            "Empty, metadata-only and blank-cue documents have no usable lyrics");
        var invalidEnd = parser.Parse("[00:01]<00:01>A<00:09>B\n[00:05]Next");
        Check(invalidEnd.Lines[0].Segments.Count == 0 && invalidEnd.Diagnostics.Count == 1,
            "Words beyond an inferred boundary fall back to line synchronization");
        var negativeOffset = parser.Parse("[offset:-250]\n[00:00]Start\n[offset:bad]");
        Check(negativeOffset.Offset == TimeSpan.FromMilliseconds(-250) && negativeOffset.Metadata["offset"] == "-250",
            "Invalid repeated offsets do not replace a valid offset");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(parser.Parse("[123:45.678]Long").Lines[0].Start == TimeSpan.FromMilliseconds(7425678),
                "Timestamp parsing is culture independent and supports minutes over 59");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }

        using var temporary = new TemporaryTestDirectory("MusicPlayerLyricsTests");
        var directory = temporary.Path;
        var audio = Path.Combine(directory, "01 - Song.flac");
        var sidecar = Path.Combine(directory, "01 - Song.LRC");
        var source = new LocalLyricsSource();
        await File.WriteAllTextAsync(Path.Combine(directory, "Song.lrc"), "[00:01]Wrong match");
        Check((await source.LoadAsync(audio)).Status == LocalLyricsStatus.NotFound,
            "Discovery requires an exact basename, without fuzzy title matching");
        await File.WriteAllTextAsync(sidecar, "[00:01]Café 世界", new UTF8Encoding(false));
        var loaded = await source.LoadAsync(audio, TimeSpan.FromSeconds(5));
        Check(loaded.Status == LocalLyricsStatus.Loaded && loaded.Document!.Lines[0].Text == "Café 世界" &&
            loaded.Document.Lines[0].End == TimeSpan.FromSeconds(5),
            "Discovery supports uppercase extensions, UTF-8 and track duration without opening the audio");
        var selected = Path.Combine(directory, "Selected.lrc");
        await File.WriteAllTextAsync(selected, "[00:01]Selected", Encoding.Unicode);
        Check((await source.LoadAsync(audio, lyricsFilePath: selected)).Document!.Lines[0].Text == "Selected",
            "An explicit LRC takes precedence and supports BOM-marked UTF-16");
        Check((await source.LoadAsync(audio, lyricsFilePath: Path.Combine(directory, "Missing.lrc"))).Status == LocalLyricsStatus.NotFound,
            "A missing explicit selection does not silently fall back to another file");
        await File.WriteAllTextAsync(selected, "[ar:Only metadata]");
        Check((await source.LoadAsync(audio, lyricsFilePath: selected)).Status == LocalLyricsStatus.Invalid,
            "Files without timed lyrics have an invalid result");
        await File.WriteAllBytesAsync(selected, new byte[] { 0xFF, 0xFF, 0xFF });
        Check((await source.LoadAsync(audio, lyricsFilePath: selected)).Status == LocalLyricsStatus.Invalid,
            "Invalid UTF-8 does not silently replace lyric characters");
        await File.WriteAllTextAsync(selected, "[00:01]Updated\nmalformed");
        var updated = await source.LoadAsync(audio, lyricsFilePath: selected);
        Check(updated.Status == LocalLyricsStatus.Loaded && updated.Document!.Diagnostics.Count == 1 && updated.Document.Lines[0].Text == "Updated",
            "Subsequent loads read edits and expose warnings alongside usable lyrics");
        using (var locked = new FileStream(selected, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check((await source.LoadAsync(audio, lyricsFilePath: selected)).Status == LocalLyricsStatus.Unavailable,
                "Unreadable lyrics are distinct from missing lyrics");
        Check((await source.LoadAsync(audio, lyricsFilePath: Path.Combine(directory, "lyrics.txt"))).Status == LocalLyricsStatus.Invalid,
            "Explicit non-LRC files are rejected");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await source.LoadAsync(audio, cancellationToken: cancellation.Token);
            throw new InvalidOperationException("Expected lyrics loading cancellation.");
        }
        catch (OperationCanceledException) { }
        try
        {
            parser.Parse("", cancellationToken: cancellation.Token);
            throw new InvalidOperationException("Expected lyrics parsing cancellation.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("Lyrics discovery and parser tests passed.");
    }
}
