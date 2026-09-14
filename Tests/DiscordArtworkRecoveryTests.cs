using MusicPlayer.Services;

internal static partial class Program
{
    private static async Task CheckDiscordArtworkRecoveryAsync()
    {
        using var pipe = new PresenceTestPipe { Available = true };
        using var client = new DiscordPresenceClient("123456789012345678", pipe);
        var now = DateTimeOffset.UtcNow;
        client.Update(new("Song with cover", "Artist — Album", now, now.AddMinutes(3), CoverUrl("release", CoverRelease), "Album"));
        client.Initialize();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pipe.Activities.Count == 0 && DateTime.UtcNow < deadline)
        {
            client.Pump();
            await Task.Delay(25);
        }
        Check(pipe.Activities.Count > 0, "Artwork test connects and publishes the cover");
        // DiscordRPC reads on a one-second interval. Process the acknowledgement
        // while the same presence is still current, before disconnecting or changing it.
        await Task.Delay(1300);
        client.Pump();
        Check(client.IsConnected && pipe.Activities.Last()["assets"] is not null,
            "An acknowledgement with no optional small image preserves the artwork session");

        async Task WaitFor(Func<bool> predicate, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(6);
            while (!predicate() && DateTime.UtcNow < until)
            {
                client.Pump();
                await Task.Delay(25);
            }
            Check(predicate(), description);
        }
        var status = "";
        client.StatusChanged += value => status = value;
        var rejectedCover = CoverUrl("release-group", CoverGroup);
        pipe.RejectArtwork = true;
        client.Update(new("Song with rejected cover", "Artist — Album", now, now.AddMinutes(3), rejectedCover, "Album"));
        await WaitFor(() => pipe.Activities.Last()["details"]?.ToString() == "Song with rejected cover" &&
            pipe.Activities.Last()["assets"] is null, "A Discord image rejection restores the latest song as text-only");
        await Task.Delay(1300);
        client.Pump();
        Check(client.IsConnected && status.Contains("album cover unavailable") &&
            pipe.Activities.All(a => a.Type != Newtonsoft.Json.Linq.JTokenType.Null),
            "Image rejection keeps IPC connected and never clears presence");
        var attempts = pipe.Activities.Count(a => a["assets"]?["large_image"]?.ToString() == rejectedCover);
        client.Update(new("Song with rejected cover", "Artist — Album", now.AddSeconds(-30), now.AddMinutes(2), rejectedCover, "Album"));
        await Task.Delay(1300);
        client.Pump();
        Check(pipe.Activities.Count(a => a["assets"]?["large_image"]?.ToString() == rejectedCover) == attempts,
            "Seeking does not retry a rejected image or loop on errors");
        pipe.RejectArtwork = false;
        client.Update(new("Another album", "Other artist", now, now.AddMinutes(3), CoverUrl("release", CoverRelease), "Other album"));
        await WaitFor(() => pipe.Activities.Last()["details"]?.ToString() == "Another album" &&
            pipe.Activities.Last()["assets"] is not null, "A different album can still publish artwork after an image rejection");
        await Task.Delay(1300);
        client.Pump();
        Check(status == "Connected to Discord", "A successful artwork acknowledgement clears the fallback status");
        client.Update(null);
        pipe.RejectActivity(); // A late response to an older artwork request must not revive playback.
        await WaitFor(() => pipe.Activities.Last().Type == Newtonsoft.Json.Linq.JTokenType.Null,
            "Pause clears presence while an artwork error is pending");
        await Task.Delay(1300);
        client.Pump();
        Check(pipe.Activities.Last().Type == Newtonsoft.Json.Linq.JTokenType.Null,
            "Late artwork errors cannot bring back a paused song");
    }
}
