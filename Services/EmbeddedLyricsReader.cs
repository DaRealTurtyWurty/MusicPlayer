using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using MusicPlayer.Models;
using TagLib.Id3v2;

namespace MusicPlayer.Services;

/// <summary>Reads existing audio tags only; never saves or changes the audio file.</summary>
internal sealed partial class EmbeddedLyricsReader
{
    private static readonly string[] Fields = ["TTML", "LYRICSFILE", "SYNCEDLYRICS", "SYNCHRONIZEDLYRICS", "LYRICS", "UNSYNCEDLYRICS", "UNSYNCED LYRICS"];
    private const int MaxCharacters = 4 * 1024 * 1024;

    public LocalLyricsResult Load(string audioPath, TimeSpan? duration, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var file = TagLib.File.Create(new ReadOnlyAudioFile(audioPath), TagLib.ReadStyle.None);
        var candidates = new List<(LyricsDocument Document, string Field, string? Language)>();
        var warnings = new List<LyricsDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Text(string? value, string field, string? language = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) return;
            if (value.Length > MaxCharacters)
            {
                warnings.Add(new(1, $"Skipped oversized embedded lyrics in {field}."));
                return;
            }
            var document = ParseText(value, field, duration, cancellation);
            if (document.HasLyrics || document.IsInstrumental) candidates.Add((document, field, language));
            else warnings.AddRange(document.Diagnostics.Count > 0 ? document.Diagnostics : [new(1, $"No usable lyrics in {field}.")]);
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<SynchronisedLyricsFrame>())
            {
                cancellation.ThrowIfCancellationRequested();
                if (frame.Type != SynchedTextType.Lyrics || frame.Text.Length == 0) continue;
                if (frame.Format != TimestampFormat.AbsoluteMilliseconds)
                {
                    warnings.Add(new(1, "Skipped SYLT lyrics with unsupported MPEG-frame or unknown timing units."));
                    continue;
                }
                var document = ParseSynchronized(frame.Text, duration, cancellation);
                if (document.HasLyrics) candidates.Add((document, "ID3v2/SYLT", frame.Language));
                else warnings.AddRange(document.Diagnostics);
            }
            foreach (var frame in id3.GetFrames<UnsynchronisedLyricsFrame>()) Text(frame.Text, "ID3v2/USLT", frame.Language);
            foreach (var field in Fields)
                foreach (var frame in id3.GetFrames<UserTextInformationFrame>().Where(frame => frame.Description.Equals(field, StringComparison.OrdinalIgnoreCase)))
                    foreach (var value in frame.Text) Text(value, $"ID3v2/TXXX/{field}");
        }
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            foreach (var field in Fields)
                foreach (var value in xiph.GetField(field)) Text(value, $"Vorbis/{field}");
        if (file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
            foreach (var field in Fields)
                foreach (var value in ape.GetItem(field)?.ToStringArray() ?? []) Text(value, $"APEv2/{field}");
        // Includes MP4/M4A ©lyr, ASF WM/Lyrics, and other standard TagLib lyric fields.
        Text(file.Tag.Lyrics, "Lyrics");
        cancellation.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
            return new(warnings.Count == 0 ? LocalLyricsStatus.NotFound : LocalLyricsStatus.Invalid, audioPath,
                Error: warnings.FirstOrDefault()?.Message) { IsEmbedded = true };

        // Select one rendition, never concatenate different languages or duplicate tag copies.
        var selected = candidates.OrderByDescending(candidate => candidate.Document.IsInstrumental ? 4 :
            candidate.Document.TimingMode == LyricsTimingMode.Segment ? 3 : candidate.Document.TimingMode == LyricsTimingMode.Line ? 2 : 1).First();
        var metadata = new Dictionary<string, string>(selected.Document.Metadata, StringComparer.OrdinalIgnoreCase)
        { ["source"] = "embedded", ["embedded_tag"] = selected.Field };
        if (!string.IsNullOrWhiteSpace(selected.Language)) metadata.TryAdd("language", selected.Language);
        var result = selected.Document with
        {
            Metadata = new ReadOnlyDictionary<string, string>(metadata),
            Diagnostics = Array.AsReadOnly(selected.Document.Diagnostics.Concat(warnings).ToArray())
        };
        return new(LocalLyricsStatus.Loaded, audioPath, result) { IsEmbedded = true };
    }

    private static LyricsDocument ParseText(string value, string field, TimeSpan? duration, CancellationToken cancellation)
    {
        var content = value.TrimStart('\uFEFF').TrimEnd('\0');
        var trimmed = content.TrimStart();
        if (trimmed.Equals("[au: instrumental]", StringComparison.OrdinalIgnoreCase))
            return new([], new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()), TimeSpan.Zero, []) { IsInstrumental = true };
        if (field.EndsWith("/TTML", StringComparison.Ordinal) || TtmlHeader().IsMatch(trimmed)) return new TtmlParser().Parse(content, duration, cancellation);
        if (field.EndsWith("/LYRICSFILE", StringComparison.Ordinal) || LyricsfileHeader().IsMatch(content)) return new LyricsfileParser().Parse(content, duration, cancellation);
        if (LrcTimestamp().IsMatch(content)) return new LrcParser().Parse(content, duration, cancellation);
        return new([], new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()), TimeSpan.Zero, []) { PlainText = content };
    }

    private static LyricsDocument ParseSynchronized(SynchedText[] entries, TimeSpan? duration, CancellationToken cancellation)
    {
        var warnings = new List<LyricsDiagnostic>();
        var valid = new List<(TimeSpan Start, string Text)>();
        var length = 0L;
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            length += entry.Text?.Length ?? 0;
            if (length > MaxCharacters || entries.Length > 100000)
                return new([], new Dictionary<string, string>(), TimeSpan.Zero, [new(1, "Embedded SYLT lyrics exceed the supported size.")]);
            if (entry.Time < 0 || entry.Time > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            {
                warnings.Add(new(1, "Skipped an invalid SYLT timestamp."));
                continue;
            }
            valid.Add((TimeSpan.FromTicks(entry.Time * TimeSpan.TicksPerMillisecond), (entry.Text ?? "").ReplaceLineEndings("\n")));
        }
        valid = valid.OrderBy(entry => entry.Start).ToList();
        var lines = new List<LyricLine>();
        var hasLineBreaks = valid.Any(entry => entry.Text.Contains('\n'));
        if (!hasLineBreaks)
        {
            // LRCGET writes one full line per entry, without newline markers.
            foreach (var entry in valid) lines.Add(new(entry.Text, entry.Start, null, true, []));
        }
        else
        {
            // ID3 word/syllable timing uses newline markers to delimit lyric lines.
            var words = new List<LyricSegment>();
            void Flush()
            {
                if (words.Count == 0) return;
                lines.Add(new(string.Concat(words.Select(word => word.Text)), words[0].Start, null, true, words.ToArray()));
                words.Clear();
            }
            foreach (var entry in valid)
            {
                cancellation.ThrowIfCancellationRequested();
                var parts = entry.Text.Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0) Flush();
                    if (parts[i].Length > 0) words.Add(new(parts[i], entry.Start, null, true));
                }
                // A timestamped empty string can end the preceding sung line.
                if (entry.Text.Trim('\n').Length == 0)
                {
                    Flush();
                    lines.Add(new("", entry.Start, null, true, []));
                }
            }
            Flush();
        }
        var starts = lines.Select(line => line.Start).Distinct().Order().ToArray();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var next = Array.BinarySearch(starts, line.Start) + 1;
            var lastWordStart = line.Segments.Count == 0 ? line.Start : line.Segments.Max(word => word.Start);
            var end = next < starts.Length ? starts[next] : duration > lastWordStart ? duration : null;
            var wordStarts = line.Segments.Select(word => word.Start).Distinct().Order().ToArray();
            var words = line.Segments.Select(word =>
            {
                var nextWord = Array.BinarySearch(wordStarts, word.Start) + 1;
                return word with { End = nextWord < wordStarts.Length ? wordStarts[nextWord] : end };
            }).ToArray();
            lines[i] = line with { End = end, Segments = Array.AsReadOnly(words) };
        }
        return new(lines.AsReadOnly(), new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["format"] = "sylt" }),
            TimeSpan.Zero, warnings.AsReadOnly());
    }

    [GeneratedRegex(@"(?m)^\s*\[[0-9]+:", RegexOptions.CultureInvariant)]
    private static partial Regex LrcTimestamp();
    [GeneratedRegex(@"(?m)^version\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex LyricsfileHeader();
    [GeneratedRegex(@"\A<(?:\?xml\b|(?:[\w-]+:)?tt(?:\s|>))", RegexOptions.CultureInvariant)]
    private static partial Regex TtmlHeader();

    private sealed class ReadOnlyAudioFile(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        public Stream WriteStream => throw new NotSupportedException("Embedded lyrics are read-only.");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
