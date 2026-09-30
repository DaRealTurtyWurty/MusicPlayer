using System.IO;
using NAudio.Wave;

namespace MusicPlayer.Services;

// One decoder thread produces samples; one output thread consumes them. Publish
// ring positions only after copying, so normal audio reads never acquire a lock.
internal sealed class ReadAheadSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly float[] _buffer;
    private readonly AutoResetEvent _dataAvailable = new(false);
    private readonly AutoResetEvent _spaceAvailable = new(false);
    private readonly Task _worker;
    private long _readPosition, _writePosition;
    private long _waitingReads;
    private volatile bool _finished;
    private int _stopping, _disposed;
    private Exception? _error;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public long WaitingReads => Interlocked.Read(ref _waitingReads);
    public double BufferedMilliseconds => Math.Max(0, Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition))
        * 1000d / (WaveFormat.SampleRate * WaveFormat.Channels);

    public ReadAheadSampleProvider(ISampleProvider source)
    {
        _source = source;
        _buffer = new float[checked(source.WaveFormat.SampleRate * source.WaveFormat.Channels * 5)];
        _worker = Task.Factory.StartNew(Fill, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Fill()
    {
        var chunk = new float[Math.Max(1, WaveFormat.SampleRate / 10) * WaveFormat.Channels];
        try
        {
            while (Volatile.Read(ref _stopping) == 0)
            {
                var written = Volatile.Read(ref _writePosition);
                var available = (int)(_buffer.Length - (written - Volatile.Read(ref _readPosition)));
                if (available == 0) { _spaceAvailable.WaitOne(); continue; }
                var read = _source.Read(chunk.AsSpan(0, Math.Min(chunk.Length, available)));
                if (Volatile.Read(ref _stopping) != 0) return;
                if (read == 0) { _finished = true; return; }
                var tail = (int)(written % _buffer.Length);
                var first = Math.Min(read, _buffer.Length - tail);
                chunk.AsSpan(0, first).CopyTo(_buffer.AsSpan(tail));
                chunk.AsSpan(first, read - first).CopyTo(_buffer);
                Volatile.Write(ref _writePosition, written + read);
                _dataAvailable.Set();
            }
        }
        catch (Exception ex) { _error = ex; _finished = true; }
        finally { _dataAvailable.Set(); }
    }

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty || Volatile.Read(ref _stopping) != 0) return 0;
        var consumed = Volatile.Read(ref _readPosition);
        var waited = false;
        long available;
        while ((available = Volatile.Read(ref _writePosition) - consumed) == 0)
        {
            if (_finished)
            {
                // The final write may have been published after our empty
                // snapshot. Observe it before reporting EOF.
                available = Volatile.Read(ref _writePosition) - consumed;
                break;
            }
            if (Volatile.Read(ref _stopping) != 0) break;
            // Only a genuinely empty buffer waits. Starvation must not signal EOF.
            if (!waited) { Interlocked.Increment(ref _waitingReads); waited = true; }
            _dataAvailable.WaitOne();
        }
        if (Volatile.Read(ref _stopping) != 0) return 0;
        if (Volatile.Read(ref _error) is { } error) throw new IOException("Audio read-ahead failed.", error);
        var read = (int)Math.Min(buffer.Length, available);
        var head = (int)(consumed % _buffer.Length);
        var first = Math.Min(read, _buffer.Length - head);
        _buffer.AsSpan(head, first).CopyTo(buffer);
        _buffer.AsSpan(0, read - first).CopyTo(buffer[first..]);
        Volatile.Write(ref _readPosition, consumed + read);
        _spaceAvailable.Set();
        return read;
    }

    // Wake the audio callback before its output thread is joined. Handles are
    // released only after the caller has stopped that thread.
    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _dataAvailable.Set();
        _spaceAvailable.Set();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RequestStop();
        _worker.GetAwaiter().GetResult();
        _dataAvailable.Dispose();
        _spaceAvailable.Dispose();
    }
}
