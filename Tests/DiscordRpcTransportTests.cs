using System.Collections.Concurrent;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using MusicPlayer.Services;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static async Task CheckDiscordRpcTransportAsync()
    {
        using var pipe = new PresenceTestPipe();
        using var client = new DiscordPresenceClient("123456789012345678", pipe);
        var callbackThread = 0;
        client.StatusChanged += _ => callbackThread = Environment.CurrentManagedThreadId;
        var now = DateTimeOffset.UtcNow;
        client.Update(new("Old offline song", null, now, now.AddMinutes(3)));
        client.Update(new("Latest song", "Artist — Album", now, now.AddMinutes(3), CoverUrl("release", CoverRelease), "Album"));
        client.Initialize();
        async Task WaitFor(Func<bool> predicate, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                client.Pump();
                await Task.Delay(25);
            }
            if (!predicate()) Console.WriteLine($"Pipe connected: {pipe.IsConnected}; frames: {string.Join("\n", pipe.Frames.TakeLast(6))}");
            Check(predicate(), message);
        }
        pipe.Available = true;
        await WaitFor(() => pipe.Activities.Count > 0, "Real RPC wrapper completes a simulated Discord handshake");
        var activity = pipe.Activities.Single();
        Check((string?)activity["details"] == "Latest song" && (int?)activity["type"] == 2 &&
              (string?)activity["state"] == "Artist — Album" && activity["timestamps"]?["end"] is not null,
            "RPC wire payload contains only the latest song, listening type and timestamps");
        Check(callbackThread == Environment.CurrentManagedThreadId, "Real RPC callbacks execute on the dispatcher thread");
        Check((string?)activity["assets"]?["large_image"] == CoverUrl("release", CoverRelease) &&
              (string?)activity["assets"]?["large_text"] == "Album", "RPC wire payload sends the public album cover and tooltip");
        pipe.Disconnect(abrupt: true);
        await WaitFor(() => !client.IsConnected, "Real RPC wrapper detects a disconnected Discord client");
        client.Update(new("Skipped during outage", null, now, now.AddMinutes(3)));
        client.Update(new("After outage", null, now, now.AddMinutes(3)));
        var before = pipe.Activities.Count;
        pipe.Available = true;
        await WaitFor(() => pipe.Activities.Count > before, "RPC background connection retries without recreating the wrapper");
        Check(pipe.Activities.Skip(before).All(a => (string?)a["details"] == "After outage"),
            "Real RPC reconnect does not replay cached or queued old songs");
        Check(pipe.Activities.Skip(before).All(a => a["assets"] is null), "A track without artwork clears previous RPC assets");
        client.Update(null);
        await WaitFor(() => pipe.Activities.Last().Type == JTokenType.Null, "Real RPC clear sends a null activity");
        pipe.Disconnect();
        await WaitFor(() => !client.IsConnected, "Cleared RPC session can disconnect again");
        before = pipe.Activities.Count;
        pipe.Available = true;
        await WaitFor(() => pipe.Activities.Count > before, "Cleared RPC session reconnects");
        Check(pipe.Activities.Skip(before).All(a => a.Type == JTokenType.Null), "Paused reconnect never revives an acknowledged song");
        client.Update(new("Before shutdown", null, now, now.AddMinutes(3)));
        await WaitFor(() => pipe.Activities.Last().Type != JTokenType.Null, "RPC can resume after reconnect");
        client.Dispose();
        await WaitFor(() => pipe.Activities.Last().Type == JTokenType.Null && !pipe.IsConnected,
            "RPC graceful shutdown sends an empty activity and closes the pipe");
    }

    private sealed class PresenceTestPipe : INamedPipeClient
    {
        private readonly ConcurrentQueue<PipeFrame> _incoming = new();
        private volatile bool _connected;
        public volatile bool Available;
        public bool RejectArtwork { get; set; }
        public ConcurrentQueue<JToken> Activities { get; } = new();
        public ConcurrentQueue<string> Frames { get; } = new();
        public ILogger Logger { get; set; } = new NullLogger();
        public bool IsConnected => _connected;
        public int ConnectedPipe => _connected ? 0 : -1;
        public bool Connect(int pipe) => _connected = Available;
        public bool ReadFrame(out PipeFrame frame) => _incoming.TryDequeue(out frame);
        public bool WriteFrame(PipeFrame frame)
        {
            Frames.Enqueue($"{frame.Opcode}: {frame.Message}");
            if (!_connected) return false;
            if (frame.Opcode == Opcode.Handshake)
                _incoming.Enqueue(new PipeFrame(Opcode.Frame, new
                {
                    cmd = "DISPATCH", evt = "READY", data = new
                    {
                        v = 1, config = new { cdn_host = "cdn.discordapp.com", api_endpoint = "//discord.com/api", environment = "production" },
                        user = new { id = "123456789", username = "Test", discriminator = "0001" }
                    }
                }));
            else if (frame.Opcode == Opcode.Close)
                _connected = false; // Discord closes its side after the client's goodbye.
            else if (frame.Opcode == Opcode.Frame)
            {
                var payload = JObject.Parse(frame.Message);
                if ((string?)payload["cmd"] == "SET_ACTIVITY")
                {
                    var activity = payload["args"]!["activity"]!;
                    Activities.Enqueue(activity.DeepClone());
                    if (RejectArtwork && activity.Type != JTokenType.Null && activity["assets"] is not null)
                        RejectActivity((string?)payload["nonce"]);
                    else _incoming.Enqueue(new PipeFrame(Opcode.Frame, new
                    {
                        cmd = "SET_ACTIVITY", data = activity, nonce = (string?)payload["nonce"]
                    }));
                }
            }
            return true;
        }
        public void RejectActivity(string? nonce = null) => _incoming.Enqueue(new PipeFrame(Opcode.Frame, new
        {
            cmd = "SET_ACTIVITY", evt = "ERROR", nonce,
            data = new { code = 4000, message = "Invalid activity asset: image could not be fetched" }
        }));
        public void Disconnect(bool abrupt = false)
        {
            Available = false;
            if (abrupt) _connected = false;
            else _incoming.Enqueue(new PipeFrame(Opcode.Close, new { code = 4000, message = "Simulated Discord shutdown" }));
        }
        public void Close() => _connected = false;
        public void Dispose() => Close();
    }
}
