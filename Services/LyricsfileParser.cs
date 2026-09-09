using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MusicPlayer.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace MusicPlayer.Services;

/// <summary>Reads the Lyricsfile 1.0 draft. All times are absolute track milliseconds.</summary>
public sealed partial class LyricsfileParser
{
    public LyricsDocument Parse(string text, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<LyricsDiagnostic>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<LyricLine>();
        string? plain = null;
        var instrumental = false;
        try
        {
            if (text.Length > 4 * 1024 * 1024) throw new YamlException("Lyricsfile exceeds the 4 MiB character limit.");
            var yaml = new YamlStream();
            yaml.Load(new LimitedParser(new StringReader(text.TrimStart('\uFEFF')), cancellationToken));
            if (yaml.Documents.Count != 1) throw new YamlException("Lyricsfile must contain exactly one YAML document.");
            var root = Mapping(yaml.Documents[0].RootNode);
            ValidateNodes(root, cancellationToken);
            if (Text(Required(root, "version")) != "1.0") throw Error(root, "Unsupported Lyricsfile version; expected the string '1.0'.");
            var info = Mapping(Required(root, "metadata"));
            metadata["format"] = "lyricsfile";
            metadata["version"] = "1.0";
            metadata["title"] = Text(Required(info, "title"));
            metadata["artist"] = Text(Required(info, "artist"));
            foreach (var key in new[] { "album", "language" })
                if (Optional(info, key) is { } value) metadata[key] = Text(value);
            if (Optional(info, "duration_ms") is { } durationNode)
            {
                var fileDuration = Milliseconds(durationNode);
                metadata["duration_ms"] = fileDuration.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
                if (duration is null or { Ticks: <= 0 }) duration = fileDuration;
            }
            if (Optional(info, "offset_ms") is { } offsetNode)
            {
                var offset = Integer(offsetNode);
                metadata["offset_ms"] = offset.ToString(CultureInfo.InvariantCulture);
                if (offset != 0) diagnostics.Add(new(LineNumber(offsetNode), "offset_ms was retained but not applied: its meaning is undefined in Lyricsfile 1.0."));
            }
            if (Optional(info, "instrumental") is { } instrumentalNode)
            {
                if (ScalarKind(instrumentalNode) != "bool") throw Error(instrumentalNode, "instrumental must be a boolean.");
                instrumental = TextValue(instrumentalNode).Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            if (Optional(root, "plain") is { } plainNode) plain = Text(plainNode);
            var entries = Optional(root, "lines") is { } linesNode ? Sequence(linesNode).Children : [];
            if (instrumental && (entries.Count > 0 || !string.IsNullOrEmpty(plain)))
                throw Error(root, "Instrumental Lyricsfiles cannot also contain lyrics.");
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { lines.Add(ReadLine(entry, duration, diagnostics, cancellationToken)); }
                catch (YamlException ex) { diagnostics.Add(new(Math.Max(1, (int)ex.Start.Line), ex.Message)); }
            }
        }
        catch (YamlException ex)
        {
            lines.Clear();
            plain = null;
            instrumental = false;
            diagnostics.Add(new(Math.Max(1, (int)ex.Start.Line), $"Invalid Lyricsfile: {ex.Message}"));
        }
        return new LyricsDocument(Array.AsReadOnly(lines.OrderBy(line => line.Start).ToArray()),
            new ReadOnlyDictionary<string, string>(metadata), TimeSpan.Zero, diagnostics.AsReadOnly())
        { PlainText = plain, IsInstrumental = instrumental };
    }

    private static LyricLine ReadLine(YamlNode node, TimeSpan? duration, List<LyricsDiagnostic> diagnostics, CancellationToken cancellation)
    {
        var map = Mapping(node);
        var text = Text(Required(map, "text"));
        var start = Milliseconds(Required(map, "start_ms"));
        var end = Optional(map, "end_ms") is { } endNode ? Milliseconds(endNode) : (TimeSpan?)null;
        if (end < start) throw Error(node, "Line end_ms precedes start_ms.");
        var words = new List<LyricSegment>();
        if (Optional(map, "words") is { } wordsNode)
        {
            try
            {
                foreach (var wordNode in Sequence(wordsNode).Children)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var word = Mapping(wordNode);
                    var wordStart = Milliseconds(Required(word, "start_ms"));
                    var wordEnd = Optional(word, "end_ms") is { } wordEndNode ? Milliseconds(wordEndNode) : (TimeSpan?)null;
                    if (wordEnd < wordStart || wordStart < start || wordStart > end || wordEnd > end)
                        throw Error(wordNode, "Word timing is reversed or outside its line interval.");
                    words.Add(new(Text(Required(word, "text")), wordStart, wordEnd, wordEnd is null));
                }
                if (words.Count > 0 && string.Concat(words.Select(word => word.Text)) is { } wordText && wordText != text)
                {
                    diagnostics.Add(new(LineNumber(wordsNode), "Word text differs from line text; displaying the timed word text."));
                    text = wordText;
                }
            }
            catch (YamlException ex)
            {
                words.Clear();
                diagnostics.Add(new(Math.Max(1, (int)ex.Start.Line), $"Using line timing: {ex.Message}"));
            }
        }
        var inferred = end is null;
        if (end is null)
        {
            // Another singer's onset is not an end boundary. Prefer the word intervals;
            // otherwise allow five seconds of presentation, capped by the audio duration.
            var lastStart = words.Count == 0 ? start : words.Max(word => word.Start);
            var fallback = TimeSpan.FromTicks(lastStart.Ticks + Math.Min(TimeSpan.FromSeconds(5).Ticks, TimeSpan.MaxValue.Ticks - lastStart.Ticks));
            end = words.Count > 0 && words.All(word => word.End is not null) ? words.Max(word => word.End) : fallback;
            if (words.Any(word => word.End > end)) end = words.Max(word => word.End);
            if (duration > start && duration < end && (words.Count == 0 || duration >= words.Max(word => word.End ?? word.Start))) end = duration;
        }
        var orderedStarts = words.Select(word => word.Start).Distinct().Order().ToArray();
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].End is not null) continue;
            var nextIndex = Array.BinarySearch(orderedStarts, words[i].Start) + 1;
            words[i] = words[i] with { End = nextIndex < orderedStarts.Length ? orderedStarts[nextIndex] : end };
        }
        return new(text, start, end, inferred, words.AsReadOnly());
    }

    private static YamlMappingNode Mapping(YamlNode node) => node as YamlMappingNode ?? throw Error(node, "Expected a mapping.");
    private static YamlSequenceNode Sequence(YamlNode node) => node as YamlSequenceNode ?? throw Error(node, "Expected a sequence.");
    private static YamlNode Required(YamlMappingNode map, string key) => Optional(map, key) ?? throw Error(map, $"Missing required field '{key}'.");
    private static YamlNode? Optional(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) && ScalarKind(value) != "null" ? value : null;
    private static string TextValue(YamlNode node) => ((YamlScalarNode)node).Value ?? "";
    private static string Text(YamlNode node) => ScalarKind(node) == "str" ? TextValue(node) : throw Error(node, "Expected a string (quote numeric text).");
    private static long Integer(YamlNode node)
    {
        if (ScalarKind(node) != "int") throw Error(node, "Expected integer milliseconds.");
        var text = TextValue(node);
        try
        {
            if (text.StartsWith("0x", StringComparison.Ordinal)) return Convert.ToInt64(text[2..], 16);
            if (text.StartsWith("0o", StringComparison.Ordinal)) return Convert.ToInt64(text[2..], 8);
            return long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw Error(node, "Integer is out of range."); }
    }
    private static TimeSpan Milliseconds(YamlNode node)
    {
        var value = Integer(node);
        if (value < 0 || value > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) throw Error(node, "Timestamp is negative or out of range.");
        return TimeSpan.FromTicks(value * TimeSpan.TicksPerMillisecond);
    }
    private static string ScalarKind(YamlNode node)
    {
        if (node is not YamlScalarNode scalar) return "collection";
        if (!scalar.Tag.IsEmpty && scalar.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal)) return scalar.Tag.Value[18..];
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)) return "str";
        var text = scalar.Value ?? "";
        if (text is "" or "~" || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return "null";
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)) return "bool";
        if (IntegerPattern().IsMatch(text)) return "int";
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || text.ToLowerInvariant() is ".nan" or ".inf" or "-.inf" or "+.inf") return "float";
        return "str";
    }
    private static int LineNumber(YamlNode node) => Math.Max(1, (int)node.Start.Line);
    private static YamlException Error(YamlNode node, string message) => new(node.Start, node.End, message);

    private static void ValidateNodes(YamlNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    _ = Text(key);
                    ValidateNodes(value, cancellation);
                }
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children) ValidateNodes(child, cancellation);
                break;
            default:
                var kind = ScalarKind(node);
                if (kind is not ("str" or "int" or "bool" or "null")) throw Error(node, "Lyricsfile supports strings, integers, booleans and null, not floating-point values.");
                if (kind == "bool" && TextValue(node).ToLowerInvariant() is not ("true" or "false")) throw Error(node, "Invalid YAML boolean.");
                break;
        }
    }
    [GeneratedRegex(@"\A(?:[+-]?[0-9]+|0x[0-9a-fA-F]+|0o[0-7]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    // Inspect events before the representation model can expand aliases or recurse deeply.
    private sealed class LimitedParser(TextReader reader, CancellationToken cancellation) : IParser
    {
        private readonly Parser _parser = new(reader);
        private int _depth;
        public ParsingEvent? Current => _parser.Current;
        public bool MoveNext()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_parser.MoveNext()) return false;
            var item = _parser.Current!;
            if (item is AnchorAlias) throw new YamlException(item.Start, item.End, "YAML aliases are not supported.");
            if (item is NodeEvent node)
            {
                if (!node.Anchor.IsEmpty) throw new YamlException(item.Start, item.End, "YAML anchors are not supported.");
                if (!node.Tag.IsEmpty && node.Tag.Value is not ("tag:yaml.org,2002:str" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:null" or "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq"))
                    throw new YamlException(item.Start, item.End, "Unsupported YAML tag.");
            }
            if (item is MappingStart or SequenceStart && ++_depth > 64) throw new YamlException(item.Start, item.End, "Lyricsfile nesting exceeds 64 levels.");
            if (item is MappingEnd or SequenceEnd) _depth--;
            return true;
        }
    }
}
