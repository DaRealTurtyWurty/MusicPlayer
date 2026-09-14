using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace MusicPlayer.Services;

/// <summary>
/// DiscordRPC 1.6.1.70 exits its read loop on shutdown before draining its final
/// clear command. Serialize a final clear with pipe closure instead.
/// </summary>
internal sealed class DiscordPresencePipe(INamedPipeClient inner) : INamedPipeClient
{
    private readonly object _gate = new();
    private volatile bool _closing;
    public ILogger Logger { get => inner.Logger; set => inner.Logger = value; }
    public bool IsConnected => !_closing && inner.IsConnected;
#pragma warning disable CS0618 // Required by DiscordRPC's pipe interface.
    public int ConnectedPipe { get { lock (_gate) return inner.ConnectedPipe; } }
#pragma warning restore CS0618

    public bool Connect(int pipe)
    {
        lock (_gate) return !_closing && inner.Connect(pipe);
    }

    public bool ReadFrame(out PipeFrame frame)
    {
        lock (_gate) return inner.ReadFrame(out frame);
    }

    public bool WriteFrame(PipeFrame frame)
    {
        lock (_gate) return !_closing && inner.WriteFrame(frame);
    }

    public void ClearAndClose()
    {
        lock (_gate)
        {
            _closing = true;
            try
            {
                if (inner.IsConnected)
                    inner.WriteFrame(new PipeFrame(Opcode.Frame, new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new { pid = Environment.ProcessId, activity = (object?)null },
                        nonce = "music-player-shutdown"
                    }));
            }
            finally { inner.Close(); }
        }
    }

    public void Close() { lock (_gate) inner.Close(); }
    public void Dispose() { lock (_gate) inner.Dispose(); }
}
