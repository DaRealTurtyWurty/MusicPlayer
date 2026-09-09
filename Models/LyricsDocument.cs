namespace MusicPlayer.Models;

public enum LyricsTimingMode { Line, Segment }

/// <summary>Parsed source timestamps. Offset is retained separately and has NOT been applied.</summary>
public sealed record LyricsDocument(
    IReadOnlyList<LyricLine> Lines,
    IReadOnlyDictionary<string, string> Metadata,
    TimeSpan Offset,
    IReadOnlyList<LyricsDiagnostic> Diagnostics)
{
    public LyricsTimingMode TimingMode => Lines.Any(line => line.Segments.Count > 0)
        ? LyricsTimingMode.Segment : LyricsTimingMode.Line;

    public bool HasLyrics => Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text));
}

/// <param name="End">Null when no end can be determined from the file or track duration.</param>
public sealed record LyricLine(string Text, TimeSpan Start, TimeSpan? End, bool IsEndInferred,
    IReadOnlyList<LyricSegment> Segments)
{
    public string? VocalistId { get; init; }
    public string? VocalistName { get; init; }
    public bool IsBackground { get; init; }
    /// <summary>Parts originating in the same TTML paragraph, including its backing vocals.</summary>
    public string? VocalGroupId { get; init; }
}

public sealed record LyricSegment(string Text, TimeSpan Start, TimeSpan? End, bool IsEndInferred);

/// <param name="LineNumber">One-based source line number.</param>
public sealed record LyricsDiagnostic(int LineNumber, string Message);
