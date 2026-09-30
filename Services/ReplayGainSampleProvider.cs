using System.Diagnostics;
using MusicPlayer.Models;
using NAudio.Wave;

namespace MusicPlayer.Services;

internal sealed record ReplayGainMetadata(double TrackGain = double.NaN, double TrackPeak = double.NaN,
    double AlbumGain = double.NaN, double AlbumPeak = double.NaN)
{
    public static ReplayGainMetadata Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return new(file.Tag.ReplayGainTrackGain, file.Tag.ReplayGainTrackPeak,
                file.Tag.ReplayGainAlbumGain, file.Tag.ReplayGainAlbumPeak);
        }
        catch (Exception ex)
        {
            // Tags are optional: readable audio must still play if metadata is unsupported or damaged.
            Trace.TraceWarning($"Could not read ReplayGain tags: {ex.Message}");
            return new();
        }
    }

    public float GetScale(ReplayGainOptions options)
    {
        if (!options.Enabled) return 1;
        var album = options.Mode == ReplayGainMode.Album && double.IsFinite(AlbumGain);
        var gain = album ? AlbumGain : TrackGain;
        if (!double.IsFinite(gain)) return 1;
        var peak = album ? AlbumPeak : TrackPeak;
        // Bound damaged/extreme tags before exponentiation.
        var scale = Math.Pow(10, Math.Clamp(gain + options.PreampDb, -120, 60) / 20);
        if (options.PreventClipping && double.IsFinite(peak) && peak > 0)
            scale = Math.Min(scale, 1 / peak);
        return (float)scale;
    }
}

// Each decoder has its own gain stage, before gapless mixing/resampling and user volume.
internal sealed class ReplayGainSampleProvider(ISampleProvider source, ReplayGainMetadata metadata,
    Func<ReplayGainOptions> getOptions) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        var options = getOptions();
        var scale = metadata.GetScale(options);
        var hasGain = options.Mode == ReplayGainMode.Album && double.IsFinite(metadata.AlbumGain)
            || double.IsFinite(metadata.TrackGain);
        var clamp = options.Enabled && options.PreventClipping && hasGain;
        for (var i = 0; i < read; i++)
        {
            var sample = buffer[i] * scale;
            buffer[i] = clamp ? Math.Clamp(sample, -1f, 1f) : sample;
        }
        return read;
    }
}
