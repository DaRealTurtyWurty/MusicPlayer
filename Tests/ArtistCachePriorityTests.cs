using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckArtistCachePriorityAsync()
    {
        string LegacyKey(string name, params string[] albums) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version = 1, Artist = name.ToUpperInvariant(), Albums = albums }))));
        var legacyKey = LegacyKey("Radiohead", "OK COMPUTER");
        var cache = new MemoryArtistStore();
        var saved = new CachedArtistIdentity(new(ArtistIdentityStatus.Identified, ArtistA, "Radiohead"), DateTimeOffset.UtcNow.AddDays(12));
        cache.Entries[legacyKey] = saved;
        using var offlineHandler = new ArtistHttpHandler(_ => throw new HttpRequestException("Offline"));
        using var offlineClient = new HttpClient(offlineHandler);
        var service = new MusicBrainzArtistService(cache, offlineClient);
        Check(service.GetKnownIdentity("Radiohead", [ArtistTrack()])?.MusicBrainzId == ArtistA,
            "Valid legacy successes are immediately available without a fresh MusicBrainz lookup");
        Check(cache.Entries.Count == 2 && cache.Entries.Values.All(e => e.ExpiresAt == saved.ExpiresAt),
            "Legacy success promotion persists the new key without extending its expiry");
        cache.Entries.TryRemove(legacyKey, out _);
        Check((await new MusicBrainzArtistService(cache, offlineClient).IdentifyAsync("Radiohead", [ArtistTrack()], default)).MusicBrainzId == ArtistA &&
              offlineHandler.Requests.Count == 0, "Promoted matches survive restart entirely offline");
        Check(service.GetKnownIdentity("Radiohead", [ArtistTrack(ArtistB)])?.MusicBrainzId == ArtistB &&
              service.GetKnownIdentity("Radiohead", [ArtistTrack(ArtistA), ArtistTrack(ArtistB)])?.Status == ArtistIdentityStatus.Ambiguous,
            "New or conflicting embedded IDs still take precedence over preserved successes");
        Check(service.GetKnownIdentity("Radiohead", [ArtistTrack(album: "Different album")]) is null,
            "Preserved successes cannot leak into a different artist/album context");

        foreach (var entry in new[]
                 {
                     saved with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) },
                     saved with { Identity = new(ArtistIdentityStatus.NotFound) },
                     saved with { Identity = new(ArtistIdentityStatus.Ambiguous) },
                     saved with { Identity = new(ArtistIdentityStatus.Unavailable) },
                     saved with { Identity = new(ArtistIdentityStatus.Identified, "bad-id", "Radiohead") }
                 })
        {
            var rejected = new MemoryArtistStore();
            rejected.Entries[legacyKey] = entry;
            Check(new MusicBrainzArtistService(rejected, offlineClient).GetKnownIdentity("Radiohead", [ArtistTrack()]) is null && rejected.Entries.Count == 1,
                $"Legacy {entry.Identity.Status} entries are not promoted when unsuccessful, expired, or invalid");
        }
        var currentKey = cache.Entries.Keys.Single();
        cache.Entries[legacyKey] = saved;
        cache.Entries[currentKey] = new(new(ArtistIdentityStatus.Ambiguous), DateTimeOffset.UtcNow.AddDays(1));
        Check(new MusicBrainzArtistService(cache, offlineClient).GetKnownIdentity("Radiohead", [ArtistTrack()])?.Status == ArtistIdentityStatus.Ambiguous,
            "A newer conflicting result cannot be overwritten by an older successful match");

        var priorityStore = new MemoryArtistStore();
        priorityStore.Entries[LegacyKey("Juice WRLD", "LEGENDS NEVER DIE")] =
            new(new(ArtistIdentityStatus.Identified, ArtistA, "Juice WRLD"), DateTimeOffset.UtcNow.AddDays(30));
        using var stalled = new StalledArtistHttpHandler();
        using var stalledClient = new HttpClient(stalled);
        var priorityService = new MusicBrainzArtistService(priorityStore, stalledClient);
        var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            artistIdentityService: priorityService);
        try
        {
            var juice = new Track { FilePath = "juice.wav", Artist = "Juice WRLD", Album = "Legends Never Die", Title = "Wishing Well" };
            vm.Tracks.Add(new Track { FilePath = "aaa.wav", Artist = "AAA Uncached", Album = "Unknown", Title = "Unknown" });
            vm.Tracks.Add(juice);
            vm.Tracks.Add(new Track { FilePath = "zzz.wav", Artist = "ZZZ Tagged", Album = "Known", Title = "Known", MusicBrainzArtistId = ArtistB });
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(vm.Artists.Single(a => a.Name == "Juice WRLD").Identification.MusicBrainzId == ArtistA &&
                  vm.Artists.Single(a => a.Name == "ZZZ Tagged").Identification.MusicBrainzId == ArtistB &&
                  !vm.ArtistIdentificationReady.IsCompleted,
                "Cached and tagged artists publish before an earlier uncached artist's stalled network request");
            Check((await priorityService.IdentifyAsync("Juice WRLD", [juice], default).WaitAsync(TimeSpan.FromSeconds(2))).MusicBrainzId == ArtistA &&
                  stalled.Calls == 1, "Direct cached lookups bypass another artist's occupied network gate");
        }
        finally
        {
            var pending = vm.ArtistIdentificationReady;
            vm.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(stalled.Cancelled, "Disposing the library cancels its unresolved network pass");
        Console.WriteLine("Artist cache priority tests passed.");
    }

    private sealed class StalledArtistHttpHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The stalled request should only complete through cancellation.");
        }
    }
}
