using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using TagLib.Id3v2;

internal static partial class Program
{
    private static async Task CheckEmbeddedLyricsAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerEmbeddedLyricsTests");
        var root = temporary.Path;
        var source = new LocalLyricsSource();
        var duration = TimeSpan.FromSeconds(12);
        var mp3 = Path.Combine(root, "LRCGET.mp3");
        // Raw ID3v2.4 bytes matching LRCGET/Lofty's UTF-8 XXX-language USLT/SYLT output.
        WriteEmbeddedMp3(mp3, "Plain fallback\nAnother plain line", [(1000, "First line"), (4000, "Café 世界"), (8000, "")]);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(mp3));
        var modified = File.GetLastWriteTimeUtc(mp3);
        var loaded = await source.LoadAsync(mp3, duration);
        Check(loaded.Status == LocalLyricsStatus.Loaded && loaded.IsEmbedded && loaded.FilePath == mp3 &&
            loaded.Document!.Metadata["embedded_tag"] == "ID3v2/SYLT" && loaded.Document.Metadata["language"] == "XXX",
            "LRCGET-compatible raw MP3 SYLT frames load before plain USLT with source provenance");
        Check(loaded.Document!.Lines.Count == 3 && loaded.Document.Lines[0].Text == "First line" && loaded.Document.Lines[1].Text == "Café 世界" &&
            loaded.Document.Lines[0].End == TimeSpan.FromSeconds(4) && loaded.Document.Lines[1].End == TimeSpan.FromSeconds(8),
            "MP3 SYLT line timing preserves Unicode and timestamped blank cues");
        Check(SHA256.HashData(await File.ReadAllBytesAsync(mp3)).SequenceEqual(hash) && File.GetLastWriteTimeUtc(mp3) == modified,
            "Embedded discovery leaves audio bytes and modification timestamps unchanged");
        File.SetAttributes(mp3, File.GetAttributes(mp3) | FileAttributes.ReadOnly);
        try
        {
            using var playbackHandle = new FileStream(mp3, FileMode.Open, FileAccess.Read, FileShare.Read);
            Check((await source.LoadAsync(mp3, duration)).Status == LocalLyricsStatus.Loaded,
                "Embedded lyrics load from read-only audio while a playback read handle is open");
        }
        finally { File.SetAttributes(mp3, File.GetAttributes(mp3) & ~FileAttributes.ReadOnly); }

        var syllables = Path.Combine(root, "Syllables.mp3");
        WriteEmbeddedMp3(syllables, "", [(1000, "\nHel"), (1500, "lo "), (2000, "world"), (4000, "\nNext "), (5000, "line"), (7000, "")]);
        var synced = (await source.LoadAsync(syllables, duration)).Document!;
        Check(synced.TimingMode == LyricsTimingMode.Segment && synced.Lines[0].Text == "Hello world" &&
            synced.Lines[0].Segments.Count == 3 && synced.Lines[0].Segments[0].End == TimeSpan.FromSeconds(1.5) &&
            synced.Lines[1].Text == "Next line" && synced.Lines[1].End == TimeSpan.FromSeconds(7),
            "SYLT newline markers group independently timed syllables into lines");
        var utf16 = Path.Combine(root, "UTF16.mp3");
        WriteEmbeddedMp3(utf16, "", []);
        using (var tagFile = TagLib.File.Create(utf16))
        {
            var tag = (TagLib.Id3v2.Tag)tagFile.GetTag(TagLib.TagTypes.Id3v2, true);
            tag.Version = 3;
            tag.AddFrame(new SynchronisedLyricsFrame("Original", "eng", SynchedTextType.Lyrics, TagLib.StringType.UTF16)
            {
                Format = TimestampFormat.AbsoluteMilliseconds,
                Text = [new(1000, "\nCafé "), new(2000, "世界"), new(5000, "\n")]
            });
            tagFile.Save();
        }
        var utf16Doc = (await source.LoadAsync(utf16, duration)).Document!;
        Check(utf16Doc.Lines[0].Text == "Café 世界" && utf16Doc.Lines[0].Segments.Count == 2 && utf16Doc.Lines[0].End == TimeSpan.FromSeconds(5),
            "ID3v2.3 UTF-16 SYLT retains Unicode, syllables and terminal newline cues");
        var unsupported = Path.Combine(root, "Frame units.mp3");
        WriteEmbeddedMp3(unsupported, "Readable fallback", [(1000, "Frame-based")], timestampFormat: 1);
        var frameFallback = (await source.LoadAsync(unsupported, duration)).Document!;
        Check(frameFallback.TimingMode == LyricsTimingMode.Plain && frameFallback.PlainText == "Readable fallback" && frameFallback.Diagnostics.Count == 1,
            "MPEG-frame SYLT timing is not misread as milliseconds; plain lyrics remain available with a warning");
        var nonLyrics = Path.Combine(root, "Events.mp3");
        WriteEmbeddedMp3(nonLyrics, "Real lyrics", [(1000, "Stage event")], contentType: 5);
        Check((await source.LoadAsync(nonLyrics, duration)).Document!.PlainText == "Real lyrics", "Non-lyric synchronized ID3 events are ignored");

        var enhanced = Path.Combine(root, "Enhanced.mp3");
        WriteEmbeddedMp3(enhanced, "[offset:250]\n[00:01]<00:01>Stay <00:02>here<00:04>", [(1000, "Less detailed")]);
        var enhancedDoc = (await source.LoadAsync(enhanced, duration)).Document!;
        Check(enhancedDoc.TimingMode == LyricsTimingMode.Segment && enhancedDoc.Offset == TimeSpan.FromMilliseconds(250) && enhancedDoc.Lines[0].Text == "Stay here",
            "Enhanced LRC inside USLT takes precedence over less detailed SYLT and retains the LRC offset");
        var userText = Path.Combine(root, "Custom lyrics.mp3");
        WriteEmbeddedMp3(userText, "Plain fallback", []);
        using (var tagFile = TagLib.File.Create(userText))
        {
            var tag = (TagLib.Id3v2.Tag)tagFile.GetTag(TagLib.TagTypes.Id3v2, true);
            var frame = UserTextInformationFrame.Get(tag, "syncedlyrics", true);
            frame.Text = ["[00:02]Custom field"];
            tagFile.Save();
        }
        Check((await source.LoadAsync(userText, duration)).Document!.Lines[0].Text == "Custom field", "Case-insensitive ID3 TXXX lyric fields support downloader conventions");

        var flac = Path.Combine(root, "LRCGET.flac");
        WriteEmbeddedFlac(flac);
        SetVorbisLyrics(flac, ("UNSYNCEDLYRICS", "Plain fallback"), ("LYRICS", "[00:01]First\n[00:04]Second"));
        var flacDoc = (await source.LoadAsync(flac, duration)).Document!;
        Check(flacDoc.TimingMode == LyricsTimingMode.Line && flacDoc.Lines[0].Text == "First" && flacDoc.Metadata["embedded_tag"] == "Vorbis/LYRICS",
            "LRCGET FLAC LYRICS timing wins over its UNSYNCEDLYRICS plain copy");
        SetVorbisLyrics(flac, ("LYRICS", VocalTtml));
        Check((await source.LoadAsync(flac, TimeSpan.FromSeconds(40))).Document!.Lines.Count == 7,
            "TTML stored in a standard lyric tag retains overlapping vocals and word timing");
        SetVorbisLyrics(flac, ("LYRICS", LyricsfileFixture));
        Check((await source.LoadAsync(flac, duration)).Document!.Metadata["format"] == "lyricsfile", "Embedded Lyricsfile YAML uses the existing reader");
        SetVorbisLyrics(flac, ("LYRICS", "[au: instrumental]"));
        Check((await source.LoadAsync(flac, duration)).Document!.IsInstrumental, "Embedded LRCGET instrumental markers produce an instrumental state");
        SetVorbisLyrics(flac, ("LYRICS", "[00:99]Broken time"));
        var invalidTimed = await source.LoadAsync(flac, duration);
        Check(invalidTimed.Document!.PlainText == "Plain fallback" && invalidTimed.Document.Diagnostics.Count > 0,
            "Broken embedded timing can fall back to a separate usable plain tag with a warning");
        SetVorbisLyrics(flac, ("LYRICS", "[00:02]Freshly edited"));
        Check((await source.LoadAsync(flac, duration)).Document!.Lines[0].Text == "Freshly edited", "Embedded tag edits are reread on the next lyrics load");

        var apple = Path.Combine(root, "Apple.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EmbeddedLyrics", "silence.m4a"), apple);
        using (var tagFile = TagLib.File.Create(apple)) { tagFile.Tag.Lyrics = "[Verse]\n\nA quiet song. 世界"; tagFile.Save(); }
        var appleDoc = (await source.LoadAsync(apple, duration)).Document!;
        Check(appleDoc.TimingMode == LyricsTimingMode.Plain && appleDoc.PlainText == "[Verse]\n\nA quiet song. 世界",
            "M4A standard lyrics atoms preserve plain text, sections, Unicode and blank lines");
        var ogg = Path.Combine(root, "Vorbis.ogg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EmbeddedLyrics", "silence.ogg"), ogg);
        SetVorbisLyrics(ogg, ("LYRICS", "[00:01]<00:01>Ogg <00:02>words<00:03>"));
        Check((await source.LoadAsync(ogg, duration)).Document!.TimingMode == LyricsTimingMode.Segment,
            "Ogg Vorbis embedded lyrics use the same word-timed tag reader");
        var ape = Path.Combine(root, "APE tags.mp3");
        WriteEmbeddedMp3(ape, "", []);
        using (var tagFile = TagLib.File.Create(ape)) { tagFile.GetTag(TagLib.TagTypes.Ape, true).Lyrics = "[00:02]APE lyrics"; tagFile.Save(); }
        Check((await source.LoadAsync(ape, duration)).Document!.Lines[0].Text == "APE lyrics", "APEv2 lyrics tags are discovered");

        var sidecar = Path.ChangeExtension(mp3, ".lrc");
        await File.WriteAllTextAsync(sidecar, "[00:02]External override");
        var external = await source.LoadAsync(mp3, duration);
        Check(!external.IsEmbedded && external.Document!.Lines[0].Text == "External override", "Sidecars override embedded lyrics, even when embedded data is richer");
        await File.WriteAllTextAsync(sidecar, "broken");
        Check((await source.LoadAsync(mp3, duration)).Status == LocalLyricsStatus.Invalid, "Invalid sidecars do not silently select embedded lyrics");
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check((await source.LoadAsync(mp3, duration)).Status == LocalLyricsStatus.Unavailable, "Unreadable sidecars do not silently select embedded lyrics");
        Check((await source.LoadAsync(syllables, duration, Path.Combine(root, "Missing.lrc"))).Status == LocalLyricsStatus.NotFound,
            "Explicit missing lyric files never fall back to audio tags");
        var empty = Path.Combine(root, "No lyrics.mp3");
        WriteEmbeddedMp3(empty, "", []);
        Check((await source.LoadAsync(empty, duration)).Status == LocalLyricsStatus.NotFound, "Audio without embedded lyrics reports a normal missing-lyrics result");
        var corrupt = Path.Combine(root, "Corrupt.flac");
        await File.WriteAllTextAsync(corrupt, "not a FLAC");
        Check((await source.LoadAsync(corrupt, duration)).Status == LocalLyricsStatus.Invalid, "Damaged audio metadata is contained as a lyrics error");
        using (var locked = new FileStream(empty, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check((await source.LoadAsync(empty, duration)).Status == LocalLyricsStatus.Unavailable, "Inaccessible audio is distinguished from missing lyrics");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await source.LoadAsync(syllables, duration, cancellationToken: cancellation.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Embedded lyrics file tests passed.");
    }

    private static async Task CheckEmbeddedLyricsViewModelAsync()
    {
        await CheckEmbeddedLyricsAsync();
        using var temporary = new TemporaryTestDirectory("MusicPlayerEmbeddedLyricsPlaybackTests");
        var path = Path.Combine(temporary.Path, "Syllables.mp3");
        WriteEmbeddedMp3(path, "", [(1000, "\nStay "), (2000, "here"), (4000, "\nNext line")]);
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, monitorLibrary: false);
        vm.CurrentTrack = new Track { FilePath = path, Title = "Embedded song", Duration = TimeSpan.FromSeconds(12) };
        vm.DurationSeconds = 12;
        await vm.Lyrics.LoadingTask;
        vm.PositionSeconds = 2.5;
        Check(vm.Lyrics.HasLyrics && vm.Lyrics.ActiveLine?.Text == "Stay here" && vm.Lyrics.ActiveLine.GetSegmentProgress(1) == 0.25,
            "Now Playing synchronizes embedded SYLT word progress with playback");
        vm.Lyrics.SeekToLineCommand.Execute(vm.Lyrics.Lines[1]);
        Check(vm.PositionSeconds == 4 && !vm.IsPlaying, "Embedded lyrics seek while preserving pause state");
        vm.Lyrics.IsEnabled = false;
        Check(!vm.Lyrics.HasLyrics && vm.Lyrics.ActiveLines.Count == 0, "Hiding lyrics clears embedded playback state");
        WriteEmbeddedMp3(path, "Plain only\n\nSecond verse", []);
        vm.Lyrics.IsEnabled = true;
        await vm.Lyrics.LoadingTask;
        Check(vm.Lyrics.HasLyrics && vm.Lyrics.Lines[0].Text == "Plain only" && !vm.Lyrics.Lines[0].IsTimed && vm.Lyrics.ActiveLine is null,
            "Showing lyrics rereads changed embedded tags and displays plain text without false timing");
        Console.WriteLine("Embedded lyrics playback tests passed.");
    }

    private static void WriteEmbeddedMp3(string path, string plain, (uint Time, string Text)[] cues, byte timestampFormat = 2, byte contentType = 1)
    {
        using var frames = new MemoryStream();
        static byte[] Size(int value) => [(byte)((value >> 21) & 127), (byte)((value >> 14) & 127), (byte)((value >> 7) & 127), (byte)(value & 127)];
        void Frame(string id, byte[] bytes)
        {
            frames.Write(Encoding.ASCII.GetBytes(id)); frames.Write(Size(bytes.Length)); frames.Write(new byte[2]); frames.Write(bytes);
        }
        if (plain.Length > 0) Frame("USLT", [3, (byte)'X', (byte)'X', (byte)'X', 0, .. Encoding.UTF8.GetBytes(plain)]);
        if (cues.Length > 0)
        {
            using var data = new MemoryStream();
            data.Write(new byte[] { 3, (byte)'X', (byte)'X', (byte)'X', timestampFormat, contentType, 0 });
            foreach (var cue in cues)
            {
                data.Write(Encoding.UTF8.GetBytes(cue.Text)); data.WriteByte(0);
                var timestamp = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(timestamp, cue.Time); data.Write(timestamp);
            }
            Frame("SYLT", data.ToArray());
        }
        using var output = File.Create(path);
        output.Write("ID3"u8); output.Write(new byte[] { 4, 0, 0 }); output.Write(Size((int)frames.Length)); frames.WriteTo(output);
        // MPEG-1 Layer III frame headers: 128 kbps, 44.1 kHz. Tag-reading fixture only.
        var mpegFrame = new byte[417]; new byte[] { 0xff, 0xfb, 0x90, 0x64 }.CopyTo(mpegFrame, 0);
        for (var i = 0; i < 10; i++) output.Write(mpegFrame);
    }

    private static void WriteEmbeddedFlac(string path)
    {
        var info = new byte[34]; info[0] = info[2] = 0x10;
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10, 8), (44100UL << 44) | (15UL << 36));
        using var output = File.Create(path); output.Write("fLaC"u8); output.Write(new byte[] { 0x80, 0, 0, 34 }); output.Write(info);
    }

    private static void SetVorbisLyrics(string path, params (string Name, string Value)[] fields)
    {
        using var tagFile = TagLib.File.Create(path);
        var tag = (TagLib.Ogg.XiphComment)tagFile.GetTag(TagLib.TagTypes.Xiph, true);
        foreach (var field in fields) tag.SetField(field.Name, field.Value);
        tagFile.Save();
    }
}
