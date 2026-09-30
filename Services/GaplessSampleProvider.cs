using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicPlayer.Services;

// Reads across a track boundary in the same output buffer. The UI commits the
// transition separately, when the output clock reaches Boundary.
internal sealed class GaplessSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly object _gate = new();
    private ISampleProvider _current = source;
    private ISampleProvider? _next;
    private long _samples;
    private TimeSpan? _boundary;
    public WaveFormat WaveFormat { get; } = source.WaveFormat;
    public TimeSpan? Boundary { get { lock (_gate) return _boundary; } }

    public bool SetNext(ISampleProvider? next)
    {
        lock (_gate)
        {
            if (_boundary is not null) return false;
            _next = next is null ? null : Convert(next, WaveFormat);
            return true;
        }
    }

    public void Commit() { lock (_gate) _boundary = null; }

    internal static ISampleProvider Convert(ISampleProvider source, WaveFormat target)
    {
        if (source.WaveFormat.Channels != target.Channels)
            source = (source.WaveFormat.Channels, target.Channels) switch
            {
                (1, 2) => new MonoToStereoSampleProvider(source),
                (2, 1) => new StereoToMonoSampleProvider(source),
                _ => throw new NotSupportedException("Gapless playback requires compatible channel layouts.")
            };
        if (source.WaveFormat.SampleRate != target.SampleRate)
            source = new WdlResamplingSampleProvider(source, target.SampleRate);
        return source;
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = _current.Read(buffer[total..]);
                total += read;
                _samples += read;
                if (read != 0) continue;
                if (_next is null) break;
                _boundary = TimeSpan.FromSeconds(_samples / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
                _current = _next;
                _next = null;
            }
            return total;
        }
    }
}
