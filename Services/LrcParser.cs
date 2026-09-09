using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>Parses LRC and Enhanced LRC without file access or playback state.</summary>
public sealed partial class LrcParser
{
    public LyricsDocument Parse(string text, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<LyricsDiagnostic>();
        var entries = new List<(LyricLine Line, int SourceLine)>();
        var offset = TimeSpan.Zero;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        var number = 0;
        while (reader.ReadLine() is { } source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            if (string.IsNullOrWhiteSpace(source)) continue;
            var remaining = source.TrimStart();
            var tag = MetadataTag().Match(remaining);
            if (tag.Success)
            {
                var key = tag.Groups[1].Value;
                var value = tag.Groups[2].Value.Trim();
                if (key.Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds))
                    {
                        diagnostics.Add(new(number, "Invalid offset; expected signed integer milliseconds."));
                        continue;
                    }
                    offset = TimeSpan.FromMilliseconds(milliseconds);
                }
                metadata[key] = value;
                continue;
            }

            var starts = new List<TimeSpan>();
            while (remaining.StartsWith('['))
            {
                var close = remaining.IndexOf(']');
                if (close < 0 || !TryTimestamp(remaining[1..close], out var start)) break;
                starts.Add(start);
                remaining = remaining[(close + 1)..];
            }
            if (starts.Count == 0)
            {
                diagnostics.Add(new(number, "Ignored line without a valid LRC timestamp."));
                continue;
            }
            if (remaining.Length > 1 && remaining[0] == '[' && char.IsAsciiDigit(remaining[1]))
            {
                diagnostics.Add(new(number, "Ignored line with a malformed repeated timestamp."));
                continue;
            }

            var parsed = ParseLine(remaining, starts[0], number, diagnostics);
            foreach (var start in starts)
            {
                try
                {
                    // Repeated chorus timestamps repeat inline timings relative to the first occurrence.
                    var shift = start - starts[0];
                    entries.Add((parsed with
                    {
                        Start = start,
                        End = parsed.End + shift,
                        Segments = Array.AsReadOnly(parsed.Segments.Select(segment => segment with
                        {
                            Start = segment.Start + shift, End = segment.End + shift
                        }).ToArray())
                    }, number));
                }
                catch (OverflowException)
                {
                    diagnostics.Add(new(number, "Repeated inline timestamps exceed the supported time range."));
                }
            }
        }

        // Stable sorting preserves simultaneous vocals and repeated source timestamps.
        var sorted = entries.OrderBy(entry => entry.Line.Start).ToArray();
        TimeSpan? nextStart = null;
        for (var i = sorted.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (line, sourceLine) = sorted[i];
            if (i + 1 < sorted.Length && sorted[i + 1].Line.Start > line.Start)
                nextStart = sorted[i + 1].Line.Start;
            var end = line.End ?? nextStart ?? (duration > line.Start ? duration : null);
            var segments = line.Segments.ToArray();
            if (segments.Length > 0 && end is { } boundary && segments.Any(segment => segment.Start > boundary || segment.End > boundary))
            {
                diagnostics.Add(new(sourceLine, "Inline timing extends past the inferred line end; using line timing."));
                segments = [];
            }
            else if (segments.Length > 0 && segments[^1].End is null)
                segments[^1] = segments[^1] with { End = end, IsEndInferred = true };
            sorted[i] = (line with { End = end, Segments = Array.AsReadOnly(segments) }, sourceLine);
        }

        return new LyricsDocument(Array.AsReadOnly(sorted.Select(entry => entry.Line).ToArray()),
            new ReadOnlyDictionary<string, string>(metadata), offset, diagnostics.AsReadOnly());
    }

    private static LyricLine ParseLine(string text, TimeSpan start, int number, List<LyricsDiagnostic> diagnostics)
    {
        var matches = InlineTag().Matches(text);
        var plainText = InlineTag().Replace(text, "");
        LyricLine LineOnly() => new(plainText, start, null, true, Array.Empty<LyricSegment>());
        if (matches.Count == 0) return LineOnly();

        var segments = new List<LyricSegment>();
        // Text before the first inline tag starts at the line timestamp.
        if (matches[0].Index > 0)
            segments.Add(new(text[..matches[0].Index], start, null, true));
        var previous = start;
        TimeSpan? explicitEnd = null;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (!TryTimestamp(match.Groups[1].Value, out var time) || time < previous)
            {
                diagnostics.Add(new(number, "Invalid or decreasing inline timestamps; using line timing."));
                return LineOnly();
            }
            if (segments.Count > 0)
                segments[^1] = segments[^1] with { End = time, IsEndInferred = true };
            var textStart = match.Index + match.Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var segmentText = text[textStart..textEnd];
            if (i == matches.Count - 1 && segmentText.Length == 0)
            {
                // A trailing tag explicitly closes the preceding segment and the line.
                explicitEnd = time;
                if (segments.Count > 0) segments[^1] = segments[^1] with { IsEndInferred = false };
            }
            else if (segmentText.Length > 0)
                segments.Add(new(segmentText, time, null, true));
            previous = time;
        }
        return new(plainText, start, explicitEnd, explicitEnd is null, segments.AsReadOnly());
    }

    private static bool TryTimestamp(string text, out TimeSpan time)
    {
        time = default;
        var match = Timestamp().Match(text);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var minutes))
            return false;
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[3].Value;
        var milliseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
        var ticks = (decimal)minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond
            + milliseconds * TimeSpan.TicksPerMillisecond;
        if (ticks > long.MaxValue) return false;
        time = TimeSpan.FromTicks((long)ticks);
        return true;
    }

    [GeneratedRegex(@"\A([0-9]+):([0-5][0-9])(?:\.([0-9]{1,3}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\A\[([a-zA-Z][a-zA-Z0-9_-]*):(.*?)\]\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTag();

    [GeneratedRegex(@"<([0-9][^<>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex InlineTag();
}
