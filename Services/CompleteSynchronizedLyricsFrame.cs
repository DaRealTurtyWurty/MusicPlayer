using System.Runtime.CompilerServices;
using TagLib;
using TagLib.Id3v2;

namespace MusicPlayer.Services;

/// <summary>Preserves terminal empty cues that TagLibSharp 2.3.0's SYLT reader drops.</summary>
internal sealed class CompleteSynchronizedLyricsFrame : SynchronisedLyricsFrame
{
    // Register at assembly initialization, before concurrent metadata readers begin.
    // TagLib handles the frame header, unsynchronization and encoding as usual.
    [ModuleInitializer]
    internal static void Register() => FrameFactory.AddFrameCreator((data, offset, header, version) =>
        header.FrameId == "SYLT" ? new CompleteSynchronizedLyricsFrame(data, offset, header, version) : null);

    private CompleteSynchronizedLyricsFrame(ByteVector data, int offset, FrameHeader header, byte version)
        : base(data, offset, header, version) { }

    protected override void ParseFields(ByteVector data, byte version)
    {
        if (data.Count < 7 || data.Count > 16 * 1024 * 1024 || data[0] > 3)
            throw new CorruptFileException("Invalid or oversized SYLT frame.");
        TextEncoding = (StringType)data[0];
        Language = data.ToString(StringType.Latin1, 1, 3);
        Format = (TimestampFormat)data[4];
        Type = (SynchedTextType)data[5];
        var delimiter = ByteVector.TextDelimiter(TextEncoding);
        var position = 6;
        string ReadText()
        {
            var end = data.Find(delimiter, position, delimiter.Count);
            if (end < position) throw new CorruptFileException("Unterminated SYLT text.");
            var value = data.ToString(TextEncoding, position, end - position);
            position = end + delimiter.Count;
            return value;
        }
        Description = ReadText();
        var entries = new List<SynchedText>();
        while (position < data.Count)
        {
            var value = ReadText();
            if (data.Count - position < 4 || entries.Count >= 100000)
                throw new CorruptFileException("Truncated timestamp or too many SYLT entries.");
            entries.Add(new(data.Mid(position, 4).ToUInt(), value));
            position += 4;
        }
        Text = entries.ToArray();
    }
}
