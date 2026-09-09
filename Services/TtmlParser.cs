using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>Imports media-time TTML lyrics and Apple's lyric timing/voice extensions, not subtitle styling.</summary>
public sealed partial class TtmlParser
{
    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace Metadata = "http://www.w3.org/ns/ttml#metadata";
    private const string AppleNamespace = "http://itunes.apple.com/lyric-ttml-extensions";

    public LyricsDocument Parse(string text, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<LyricsDiagnostic>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<LyricLine>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text.TrimStart('\uFEFF')), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var root = document.Root;
            if (root?.Name != Tt + "tt") throw new FormatException("Expected a TTML tt document in the TTML namespace.");
            var context = new ImportContext(root, diagnostics, cancellationToken);
            metadata["format"] = "ttml";
            metadata["timingConvention"] = context.AppleClocks ? "apple-lyrics" : "ttml-relative";
            if ((string?)root.Attribute(XNamespace.Xml + "lang") is { } language) metadata["language"] = language;
            foreach (var item in root.Element(Tt + "head")?.Descendants() ?? [])
                if (item.Name == Metadata + "title" || item.Name == Metadata + "desc") metadata[item.Name.LocalName] = item.Value.Trim();
            var body = root.Element(Tt + "body");
            if (body is null) throw new FormatException("This TTML document has no body.");
            context.Resolve(body, TimeSpan.Zero, duration > TimeSpan.Zero ? duration : null, false, 0);
            var paragraphIndex = 0;
            foreach (var paragraph in body.DescendantsAndSelf(Tt + "p"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines.AddRange(context.ReadParagraph(paragraph, paragraphIndex++));
            }
        }
        catch (XmlException ex) { diagnostics.Add(new(Math.Max(1, ex.LineNumber), $"Invalid TTML XML: {ex.Message}")); }
        catch (FormatException ex) { diagnostics.Add(new(1, ex.Message)); }

        // Keep the lead and backing parts of each paragraph together. Independent paragraphs
        // are ordered by onset; their explicit intervals are never truncated by another singer.
        var ordered = lines.GroupBy(line => line.VocalGroupId).OrderBy(group => group.Min(line => line.Start))
            .SelectMany(group => group.OrderBy(line => line.IsBackground).ThenBy(line => line.Start)).ToArray();
        return new LyricsDocument(Array.AsReadOnly(ordered), new ReadOnlyDictionary<string, string>(metadata),
            TimeSpan.Zero, diagnostics.AsReadOnly());
    }

    private sealed class ImportContext
    {
        private sealed record Timing(TimeSpan Start, TimeSpan? End, bool EndExplicit, bool HasTiming);
        private sealed record Run(string Text, Timing Timing, bool InlineTiming, bool Preserve);
        private readonly record struct Voice(string? Agent, XElement? BackgroundOwner);
        private readonly Dictionary<XElement, Timing> _timings = [];
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly TtmlTimeParser _times;
        private readonly List<LyricsDiagnostic> _diagnostics;
        private readonly CancellationToken _cancellation;
        public bool AppleClocks { get; }

        public ImportContext(XElement root, List<LyricsDiagnostic> diagnostics, CancellationToken cancellation)
        {
            _diagnostics = diagnostics;
            _cancellation = cancellation;
            _times = new TtmlTimeParser(root);
            // Two-field MM:SS clocks are Apple's lyric shorthand, not standard TTML clocks.
            AppleClocks = root.Attributes().Any(attribute => attribute.IsNamespaceDeclaration && attribute.Value == AppleNamespace) ||
                root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.LocalName is "begin" or "end" &&
                    ShortClock().IsMatch(attribute.Value));
            foreach (var agent in root.Element(Tt + "head")?.Descendants(Metadata + "agent") ?? [])
            {
                var id = (string?)agent.Attribute(XNamespace.Xml + "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = agent.Elements(Metadata + "name").FirstOrDefault(element => (string?)element.Attribute("type") == "full")?.Value
                    ?? agent.Element(Metadata + "name")?.Value;
                _names[id] = string.IsNullOrWhiteSpace(name) ? $"Vocal {_names.Count + 1}" : Whitespace().Replace(name, " ").Trim();
            }
        }

        private void Warn(XElement element, string message) => _diagnostics.Add(new(
            Math.Max(1, ((IXmlLineInfo)element).LineNumber), message));

        public void Resolve(XElement element, TimeSpan origin, TimeSpan? parentEnd, bool inheritedTiming, int depth,
            TimeSpan? parentStart = null)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (depth > 128) { Warn(element, "TTML nesting exceeds the supported depth."); return; }
            if (element.Name.Namespace != Tt || element.Name.LocalName is not ("body" or "div" or "p" or "span" or "br")) return;
            try
            {
                var container = (string?)element.Attribute("timeContainer") ?? "par";
                if (container is not ("par" or "seq")) throw new FormatException("Unsupported TTML time container.");
                TimeSpan Absolute(string expression) => AppleClocks && expression.Contains(':')
                    ? _times.Parse(expression) : origin + _times.Parse(expression);
                var begin = (string?)element.Attribute("begin");
                var endAttribute = (string?)element.Attribute("end");
                var durAttribute = (string?)element.Attribute("dur");
                var start = begin is null ? origin : Absolute(begin);
                TimeSpan? end = endAttribute is null ? null : Absolute(endAttribute);
                if (durAttribute is not null) end = Earlier(end, start + _times.Parse(durAttribute));
                if (end < start) throw new FormatException("TTML end precedes its begin.");
                var bound = Earlier(end, parentEnd);
                var lowerBound = parentStart > origin ? parentStart.Value : origin;
                var clippedStart = start < lowerBound ? lowerBound : start;
                if (bound <= clippedStart) { Warn(element, "Ignored an empty or clipped TTML interval."); return; }
                var hasTiming = inheritedTiming || begin is not null || endAttribute is not null || durAttribute is not null;
                var cursor = start;
                var children = new List<Timing>();
                foreach (var child in element.Elements())
                {
                    Resolve(child, container == "seq" ? cursor : start, bound, hasTiming, depth + 1, clippedStart);
                    if (!_timings.TryGetValue(child, out var childTiming)) continue;
                    children.Add(childTiming);
                    if (container == "seq")
                    {
                        if (childTiming.End is not { } childEnd)
                        {
                            Warn(child, "A sequential TTML item has no end; subsequent items cannot be timed.");
                            break;
                        }
                        cursor = childEnd;
                    }
                }
                if (end is null && children.Count > 0 && children.All(child => child.End is not null) &&
                    !element.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
                    end = children.Max(child => child.End);
                end = Earlier(end, parentEnd);
                // Apple clock values can precede a container; restrict presentation to its interval.
                start = clippedStart;
                if (end <= start) { Warn(element, "Ignored an empty or clipped TTML interval."); return; }
                _timings[element] = new(start, end, endAttribute is not null || durAttribute is not null,
                    hasTiming || children.Any(child => child.HasTiming));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                Warn(element, $"Ignored invalid TTML timing: {ex.Message}");
            }
        }

        public IEnumerable<LyricLine> ReadParagraph(XElement paragraph, int index)
        {
            if (!_timings.TryGetValue(paragraph, out var paragraphTiming) || !paragraphTiming.HasTiming) yield break;
            var parts = new Dictionary<Voice, List<Run>>();
            var agent = Inherited(paragraph, Metadata + "agent");
            var background = HasRole(paragraph, "x-bg") ? paragraph : null;
            Collect(paragraph, agent, background, false, parts);
            foreach (var (voice, sourceRuns) in parts)
            {
                var runs = Normalize(sourceRuns);
                if (!runs.Any(run => !string.IsNullOrWhiteSpace(run.Text))) continue;
                var inline = runs.Any(run => run.InlineTiming);
                var content = runs.Where(run => !string.IsNullOrWhiteSpace(run.Text)).ToArray();
                var start = content.Min(run => run.Timing.Start);
                var end = content.All(run => run.Timing.End is not null) ? content.Max(run => run.Timing.End) : paragraphTiming.End;
                end = Earlier(end, paragraphTiming.End);
                if (end <= start) continue;
                var segments = inline ? runs.Select(run => new LyricSegment(run.Text, run.Timing.Start,
                    Earlier(run.Timing.End, end), !run.Timing.EndExplicit)).ToArray() : [];
                var ids = voice.Agent?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                yield return new LyricLine(string.Concat(runs.Select(run => run.Text)), start, end,
                    content.Any(run => !run.Timing.EndExplicit), Array.AsReadOnly(segments))
                {
                    VocalistId = ids.Length == 0 ? null : string.Join(' ', ids),
                    VocalistName = ids.Length == 0 ? null : string.Join(" & ", ids.Select(id => _names.GetValueOrDefault(id, id))),
                    IsBackground = voice.BackgroundOwner is not null, VocalGroupId = $"ttml-{index}"
                };
            }
        }

        private void Collect(XElement element, string? agent, XElement? background, bool inline, Dictionary<Voice, List<Run>> parts)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!_timings.TryGetValue(element, out var timing) || HasRole(element, "x-translation") || HasRole(element, "x-romanization")) return;
            agent = (string?)element.Attribute(Metadata + "agent") ?? agent;
            if (HasRole(element, "x-bg")) background = element;
            inline |= element.Name == Tt + "span" && element.Attributes().Any(attribute => attribute.Name.LocalName is "begin" or "end" or "dur");
            var preserve = Inherited(element, XNamespace.Xml + "space") == "preserve";
            var key = new Voice(agent, background);
            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    if (!parts.TryGetValue(key, out var runs)) parts[key] = runs = [];
                    runs.Add(new(text.Value, timing, inline, preserve));
                }
                else if (node is XElement child && child.Name == Tt + "br")
                {
                    if (!parts.TryGetValue(key, out var runs)) parts[key] = runs = [];
                    runs.Add(new("\n", timing, inline, true));
                }
                else if (node is XElement span && span.Name == Tt + "span") Collect(span, agent, background, inline, parts);
            }
        }

        private static List<Run> Normalize(List<Run> source)
        {
            var result = new List<Run>();
            foreach (var run in source)
            {
                var text = run.Preserve ? run.Text : Whitespace().Replace(run.Text, " ");
                if (!run.Preserve && (result.Count == 0 || result[^1].Text.EndsWith(' ') || result[^1].Text.EndsWith('\n')))
                    text = text.TrimStart(' ');
                if (text.Length > 0) result.Add(run with { Text = text });
            }
            while (result.Count > 0 && !result[^1].Preserve)
            {
                var trimmed = result[^1].Text.TrimEnd(' ');
                if (trimmed.Length > 0) { result[^1] = result[^1] with { Text = trimmed }; break; }
                result.RemoveAt(result.Count - 1);
            }
            return result;
        }

        private static string? Inherited(XElement element, XName attribute) =>
            element.AncestorsAndSelf().Select(ancestor => (string?)ancestor.Attribute(attribute)).FirstOrDefault(value => value is not null);

        private static bool HasRole(XElement element, string role) => ((string?)element.Attribute(Metadata + "role"))?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(role) == true;

        private static TimeSpan? Earlier(TimeSpan? first, TimeSpan? second) => first is null ? second : second is null ? first : first < second ? first : second;
    }

    [GeneratedRegex(@"\A[0-9]+:[0-5][0-9](?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortClock();
    [GeneratedRegex("[ \\t\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
