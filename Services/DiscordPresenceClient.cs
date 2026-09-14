using System.Diagnostics;
using System.Text;
using DiscordRPC;

namespace MusicPlayer.Services;

public sealed class DiscordPresenceClient : IDiscordPresenceClient
{
    private readonly DiscordRpcClient _client;
    private readonly DiscordPresencePipe _pipe;
    private DiscordPresence? _desired;
    private readonly HashSet<string> _rejectedArtwork = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _disposed;
    public bool IsConnected => _ready && _pipe.IsConnected;
    public Task Shutdown { get; private set; } = Task.CompletedTask;

    public event Action<string>? StatusChanged;

    public DiscordPresenceClient(string applicationId) : this(applicationId, null) { }

    internal DiscordPresenceClient(string applicationId, DiscordRPC.IO.INamedPipeClient? pipe)
    {
        // Dispatch lifecycle callbacks via Pump. The raw acknowledgement hook below
        // only edits its incoming message on the IPC worker before it is queued.
        _pipe = new DiscordPresencePipe(pipe ?? new DiscordRPC.IO.ManagedNamedPipeClient());
        _client = new DiscordRpcClient(applicationId, autoEvents: false, client: _pipe) { ShutdownOnly = true };
        _client.OnRpcMessage += (_, message) =>
        {
            // 1.6.1.70 Assets.Merge dereferences missing large/small image keys
            // (upstream issue #284). We don't use Discord's returned asset IDs.
            // Drop only that acknowledgement metadata before the library queues it;
            // outgoing artwork and our authoritative _desired snapshot stay intact.
            if (message is DiscordRPC.Message.PresenceMessage { Presence: { } presence })
                presence.Assets = null;
        };
        _client.OnReady += (_, _) =>
        {
            _ready = true;
            if (IsConnected) Publish();
            ReportConnected();
        };
        _client.OnPresenceUpdate += (_, _) => ReportConnected();
        _client.OnConnectionEstablished += (_, _) => _ready = false;
        _client.OnClose += (_, _) => Disconnected();
        _client.OnConnectionFailed += (_, _) => Disconnected();
        _client.OnError += (_, message) =>
        {
            Trace.TraceWarning($"Discord presence error: {message.Code}: {message.Message}");
            if (IsConnected && _desired?.ArtworkUrl is { } url && !_rejectedArtwork.Contains(url))
            {
                // Retry the latest song once without the image. Keep that URL suppressed
                // for this connection lifetime so seeks cannot cause a rejection loop.
                if (_rejectedArtwork.Count >= 128) _rejectedArtwork.Clear();
                _rejectedArtwork.Add(url);
                Publish();
                ReportConnected();
            }
            else
                StatusChanged?.Invoke($"Discord rejected the update: {LimitText(message.Message)}");
        };
    }

    public void Initialize()
    {
        if (!_client.Initialize()) throw new InvalidOperationException("Could not initialize Discord RPC.");
    }

    public void Pump()
    {
        if (!_disposed) _client.Invoke();
    }

    public void Update(DiscordPresence? presence)
    {
        if (_disposed) return;
        _desired = presence;
        // Retain only the latest snapshot while offline, rather than queuing old songs.
        if (IsConnected) Publish();
    }

    private void Publish()
    {
        // The library compares against its last acknowledged state. Force our latest
        // snapshot on ready as well, even if the same song was playing before reconnect.
        _client.SkipIdenticalPresence = false;
        try
        {
            if (_desired is not { } presence)
                _client.ClearPresence();
            else
                _client.SetPresence(CreateActivity(presence.ArtworkUrl is { } url && _rejectedArtwork.Contains(url)
                    ? presence with { ArtworkUrl = null, ArtworkText = null } : presence));
        }
        finally
        {
            // This also prevents the library's automatic Ready synchronization from
            // replaying its old cached presence before our OnReady publishes _desired.
            _client.SkipIdenticalPresence = true;
        }
    }

    internal static RichPresence CreateActivity(DiscordPresence presence) => new()
    {
        Type = ActivityType.Listening,
        Details = LimitText(presence.Title) ?? "Unknown track",
        State = LimitText(presence.ArtistAndAlbum),
        Assets = AlbumArtworkUrlResolver.IsCoverUrl(presence.ArtworkUrl) ? new Assets
        {
            LargeImageKey = presence.ArtworkUrl,
            LargeImageText = LimitText(presence.ArtworkText)
        } : null,
        Timestamps = presence.Start is { } start && presence.End is { } end
            ? new Timestamps(start.UtcDateTime, end.UtcDateTime) : null
    };

    internal static string? LimitText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new StringBuilder();
        var bytes = 0;
        // DiscordRPC validates UTF-8 byte length, not .NET string length.
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            var sanitized = Rune.IsControl(rune) ? new Rune(' ') : rune;
            if (bytes + sanitized.Utf8SequenceLength > 128) break;
            result.Append(sanitized.ToString());
            bytes += sanitized.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private void Disconnected()
    {
        _ready = false;
        StatusChanged?.Invoke("Waiting for Discord desktop");
        // DiscordRPC retries the pipe connection with backoff.
    }

    private void ReportConnected() => StatusChanged?.Invoke(
        _desired?.ArtworkUrl is { } url && _rejectedArtwork.Contains(url)
            ? "Connected to Discord; album cover unavailable"
            : "Connected to Discord");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _desired = null;
        _ready = false;
        StatusChanged = null;
        // Keep pipe I/O off the dispatcher. The binding waits for this shutdown before
        // opening a replacement, so an old session cannot clear a newly enabled one.
        Shutdown = Task.Run(() =>
        {
            try { _pipe.ClearAndClose(); }
            catch (Exception ex) { Trace.TraceWarning($"Could not clear Discord presence on shutdown: {ex.Message}"); }
            finally
            {
                try { _client.Dispose(); }
                catch (Exception ex) { Trace.TraceWarning($"Could not dispose Discord RPC: {ex.Message}"); }
            }
        });
    }
}
