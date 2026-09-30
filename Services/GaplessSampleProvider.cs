using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicPlayer.Services;

// Reads across a track boundary in the same output buffer. The UI commits the
// transition separately, when the output clock reaches Boundary.
internal sealed class GaplessSampleProvider(ISampleProvider source, TimeSpan? remaining = null, double crossfadeSeconds = 0) : ISampleProvider
{
    private readonly object _gate = new();
    private ISampleProvider _current = source;
    private ISampleProvider? _next;
    private long _samples;
    private long _boundaryTicks = -1;
    private long _remainingFrames = remaining is { } time ? (long)(time.TotalSeconds * source.WaveFormat.SampleRate) : long.MaxValue;
    private long _nextFrames;
    private long _fadeFrames;
    private long _fadePosition;
    private bool _fading;
    private float[] _mixBuffer = [];
    public TimeSpan NextPosition { get; private set; }
    public WaveFormat WaveFormat { get; } = source.WaveFormat;
    public TimeSpan ReadPosition => TimeSpan.FromSeconds(Interlocked.Read(ref _samples)
        / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
    // The dispatcher polls this during playback. It must never wait for disk I/O
    // performed by the decoder while Read holds the state lock.
    public TimeSpan? Boundary
    {
        get
        {
            var ticks = Volatile.Read(ref _boundaryTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public bool SetNext(ISampleProvider? next, TimeSpan? duration = null)
    {
        lock (_gate)
        {
            if (Boundary is not null || _fading) return false;
            _next = next is null ? null : Convert(next, WaveFormat);
            _nextFrames = duration is { } time ? (long)(time.TotalSeconds * WaveFormat.SampleRate) : long.MaxValue;
            // Short tracks retain a solo section before/after the overlap.
            _fadeFrames = next is null || remaining is null || duration is null ? 0 :
                Math.Max(0, Math.Min((long)(crossfadeSeconds * WaveFormat.SampleRate), Math.Min(_remainingFrames / 2, _nextFrames / 2)));
            return true;
        }
    }

    public void Commit() { lock (_gate) Volatile.Write(ref _boundaryTicks, -1); }

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
                var channels = WaveFormat.Channels;
                if (_next is not null && _fadeFrames > 0 && _remainingFrames <= _fadeFrames) _fading = true;
                if (_fading)
                {
                    var count = (int)Math.Min((buffer.Length - total) / channels, _fadeFrames - _fadePosition) * channels;
                    if (count == 0) break;
                    var target = buffer.Slice(total, count);
                    target.Clear();
                    ReadFully(_current, target);
                    if (_mixBuffer.Length < count) _mixBuffer = new float[count];
                    var incoming = _mixBuffer.AsSpan(0, count);
                    incoming.Clear();
                    ReadFully(_next!, incoming);
                    for (var i = 0; i < count; i++)
                    {
                        var weight = (float)((_fadePosition + i / channels + 1) / (double)_fadeFrames);
                        target[i] = target[i] * (1 - weight) + incoming[i] * weight;
                    }
                    _fadePosition += count / channels;
                    total += count;
                    _samples += count;
                    if (_fadePosition == _fadeFrames)
                    {
                        NextPosition = TimeSpan.FromSeconds(_fadeFrames / (double)WaveFormat.SampleRate);
                        SwitchToNext();
                    }
                    continue;
                }
                var available = buffer.Length - total;
                if (_next is not null && _fadeFrames > 0)
                    available = (int)Math.Min(available, (_remainingFrames - _fadeFrames) * channels);
                var read = _current.Read(buffer.Slice(total, available));
                total += read;
                _samples += read;
                _remainingFrames = Math.Max(0, _remainingFrames - read / channels);
                if (read != 0) continue;
                if (_next is null) break;
                NextPosition = TimeSpan.Zero;
                SwitchToNext();
            }
            return total;
        }
    }

    private void SwitchToNext()
    {
        var boundary = TimeSpan.FromSeconds(_samples / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
        _current = _next!;
        _next = null;
        _remainingFrames = Math.Max(0, _nextFrames - (_fading ? _fadeFrames : 0));
        _fading = false;
        _fadePosition = _fadeFrames = 0;
        // Publish only after the incoming track and its clock offset are ready.
        Volatile.Write(ref _boundaryTicks, boundary.Ticks);
    }

    private static void ReadFully(ISampleProvider provider, Span<float> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = provider.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
    }
}
