using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicPlayer.Services;

internal sealed partial class TtmlTimeParser
{
    private static readonly XNamespace Parameters = "http://www.w3.org/ns/ttml#parameter";
    private readonly double _frameRate;
    private readonly int _nominalFrameRate;
    private readonly int _subFrameRate;
    private readonly double _tickRate;

    public TtmlTimeParser(XElement root)
    {
        var timeBase = (string?)root.Attribute(Parameters + "timeBase");
        if (timeBase is not (null or "media")) throw new FormatException("Only media-time TTML is supported.");
        var drop = (string?)root.Attribute(Parameters + "dropMode");
        if (drop is not (null or "nonDrop")) throw new FormatException("Drop-frame TTML timing is not supported.");
        _nominalFrameRate = PositiveInteger((string?)root.Attribute(Parameters + "frameRate") ?? "30");
        _frameRate = _nominalFrameRate;
        _subFrameRate = PositiveInteger((string?)root.Attribute(Parameters + "subFrameRate") ?? "1");
        if ((string?)root.Attribute(Parameters + "frameRateMultiplier") is { } multiplier)
        {
            var values = multiplier.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 2) throw new FormatException("Invalid TTML frame-rate multiplier.");
            _frameRate *= (double)PositiveInteger(values[0]) / PositiveInteger(values[1]);
        }
        _tickRate = (string?)root.Attribute(Parameters + "tickRate") is { } tickRate ? PositiveInteger(tickRate)
            : root.Attribute(Parameters + "frameRate") is not null ? _frameRate * _subFrameRate : 1;
    }

    public TimeSpan Parse(string text)
    {
        text = text.Trim();
        var offset = Offset().Match(text);
        double seconds;
        if (offset.Success)
        {
            var value = Number(offset.Groups[1].Value);
            seconds = offset.Groups[2].Value switch
            {
                "h" => value * 3600, "m" => value * 60, "ms" => value / 1000,
                "f" => value / _frameRate, "t" => value / _tickRate, _ => value
            };
        }
        else
        {
            var fields = text.Split(':');
            if (fields.Length is < 2 or > 4) throw new FormatException($"Invalid TTML time '{text}'.");
            var hours = fields.Length >= 3 ? Integer(fields[0]) : 0;
            var minutes = Integer(fields[fields.Length == 2 ? 0 : 1]);
            var wholeSeconds = Number(fields[fields.Length == 2 ? 1 : 2]);
            if (wholeSeconds >= 60 || fields.Length >= 3 && minutes >= 60)
                throw new FormatException($"Invalid TTML clock time '{text}'.");
            seconds = hours * 3600d + minutes * 60d + wholeSeconds;
            if (fields.Length == 4)
            {
                if (fields[2].Contains('.')) throw new FormatException("Frame clock seconds must be integers.");
                var frames = fields[3].Split('.');
                if (frames.Length > 2) throw new FormatException("Invalid TTML subframe time.");
                var frame = Integer(frames[0]);
                var subframe = frames.Length == 2 ? Integer(frames[1]) : 0;
                if (frame >= _nominalFrameRate || subframe >= _subFrameRate) throw new FormatException("TTML frame or subframe is out of range.");
                seconds += (frame + subframe / (double)_subFrameRate) / _frameRate;
            }
        }
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
            throw new FormatException("TTML time is out of range.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static int PositiveInteger(string text)
    {
        var value = Integer(text);
        if (value <= 0) throw new FormatException("TTML timing rates must be positive integers.");
        return value;
    }

    private static int Integer(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException($"Invalid TTML integer '{text}'.");

    private static double Number(string text) => DecimalNumber().IsMatch(text) &&
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException($"Invalid TTML number '{text}'.");

    [GeneratedRegex(@"\A([0-9]+(?:\.[0-9]+)?)(ms|h|m|s|f|t)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalNumber();
}
