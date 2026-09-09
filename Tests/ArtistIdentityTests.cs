using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private const string ArtistA = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
    private const string ArtistB = "cc197bad-dc9c-440d-a5b5-d52ba2e14234";

    private static Track ArtistTrack(string? id = null, string album = "OK Computer") => new()
    {
        FilePath = "artist-test.wav", Title = "Airbag", Artist = "Radiohead", Album = album, MusicBrainzArtistId = id
    };

    private static string ArtistSearch(params (string Id, string Name)[] artists) => JsonSerializer.Serialize(new
    {
        count = artists.Length,
        artists = artists.Select(a => new { id = a.Id, name = a.Name, score = 100 })
    });

    private static HttpResponseMessage ArtistJson(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static async Task CheckArtistIdentityAsync()
    {
        var cache = new MemoryArtistStore();
        using var handler = new ArtistHttpHandler(_ => ArtistJson(ArtistSearch((ArtistA, "Radiohead"))));
        using var client = new HttpClient(handler);
        var service = new MusicBrainzArtistService(cache, client);
        var tagged = await service.IdentifyAsync("Radiohead", [ArtistTrack(ArtistA.ToUpperInvariant())], default);
        Check(tagged.MusicBrainzId == ArtistA && tagged.Source == ArtistIdentitySource.Tags && handler.Requests.Count == 0,
            "A valid tagged artist ID is normalized and needs no HTTP request");
        var conflict = await service.IdentifyAsync("Radiohead", [ArtistTrack(ArtistA), ArtistTrack(ArtistB)], default);
        Check(conflict.Status == ArtistIdentityStatus.Ambiguous && handler.Requests.Count == 0,
            "Conflicting tagged artist IDs cannot silently select one artist");
        var lookup = await service.IdentifyAsync("Radiohead", [ArtistTrack("invalid-id")], default);
        Check(lookup.MusicBrainzId == ArtistA && lookup.Source == ArtistIdentitySource.MusicBrainz && handler.Requests.Count == 1,
            "Invalid IDs fall back to an exact MusicBrainz artist search");
        var cached = await new MusicBrainzArtistService(cache, client).IdentifyAsync(" Radiohead ", [ArtistTrack()], default);
        Check(cached == lookup && handler.Requests.Count == 1, "A new service reuses the persisted identity cache");
        await service.IdentifyAsync("Radiohead", [ArtistTrack(ArtistB)], default);
        Check(handler.Requests.Count == 1, "New embedded IDs take precedence over cached name matches");
        cache.Entries.Clear();
        var deduplicated = new MusicBrainzArtistService(cache, client);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => deduplicated.IdentifyAsync("Radiohead", [ArtistTrack()], default)));
        Check(handler.Requests.Count == 2, "Concurrent requests for one artist share the cached lookup");
        Check(handler.Requests.Zip(handler.Requests.Skip(1)).All(p => p.Second.At - p.First.At >= TimeSpan.FromSeconds(1)),
            "Requests from different service instances obey the shared one-request-per-second limit");
        Check(handler.Requests.All(r => r.UserAgent.Contains("github.com/DaRealTurtyWurty/MusicPlayer") && r.Uri.Scheme == "https"),
            "MusicBrainz requests use HTTPS and an identifying User-Agent with a contact URL");
        foreach (var key in cache.Entries.Keys.ToArray()) cache.Entries[key] = cache.Entries[key] with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        await new MusicBrainzArtistService(cache, client).IdentifyAsync("Radiohead", [ArtistTrack()], default);
        Check(handler.Requests.Count == 3, "Expired identities are refreshed instead of reused indefinitely");
        await service.IdentifyAsync("Radiohead", [ArtistTrack(album: "Different album")], default);
        Check(handler.Requests.Count == 4, "Different album context does not inherit another artist's cached name match");

        using var ambiguousHandler = new ArtistHttpHandler(request => ArtistJson(request.RequestUri!.AbsolutePath.EndsWith("release-group")
            ? $$$"""{"count":1,"release-groups":[{"title":"OK Computer","artist-credit":[{"artist":{"id":"{{{ArtistB}}}","name":"Radiohead"}}]}]}"""
            : ArtistSearch((ArtistA, "Radiohead"), (ArtistB, "Radiohead"))));
        using var ambiguousClient = new HttpClient(ambiguousHandler);
        var disambiguated = await new MusicBrainzArtistService(client: ambiguousClient).IdentifyAsync("Radiohead", [ArtistTrack()], default);
        Check(disambiguated.MusicBrainzId == ArtistB, "Album context disambiguates artists with the same name");
        var ambiguous = await new MusicBrainzArtistService(client: ambiguousClient).IdentifyAsync("Radiohead", [], default);
        Check(ambiguous.Status == ArtistIdentityStatus.Ambiguous && ambiguous.MusicBrainzId is null,
            "Same-name artists remain unresolved without supporting context");

        using var wrongHandler = new ArtistHttpHandler(_ => ArtistJson(ArtistSearch((ArtistA, "Adele & Andy"))));
        using var wrongClient = new HttpClient(wrongHandler);
        var wrong = await new MusicBrainzArtistService(client: wrongClient).IdentifyAsync("Adele", [], default);
        Check(wrong.Status == ArtistIdentityStatus.NotFound, "A high search score cannot override a mismatched artist name");

        var missingCache = new MemoryArtistStore();
        using var missingHandler = new ArtistHttpHandler(_ => ArtistJson(ArtistSearch()));
        using var missingClient = new HttpClient(missingHandler);
        await new MusicBrainzArtistService(missingCache, missingClient).IdentifyAsync("Missing artist", [], default);
        await new MusicBrainzArtistService(missingCache, missingClient).IdentifyAsync("Missing artist", [], default);
        Check(missingHandler.Requests.Count == 1, "Missing artists are cached across restarts to avoid repeated requests");

        using var aliasHandler = new ArtistHttpHandler(_ => ArtistJson(
            $$$"""{"count":1,"artists":[{"id":"{{{ArtistA}}}","name":"Canonical name","score":"100","aliases":[{"name":"Alias"}]}]}"""));
        using var aliasClient = new HttpClient(aliasHandler);
        var alias = await new MusicBrainzArtistService(client: aliasClient).IdentifyAsync("Alias", [], default);
        Check(alias.MusicBrainzId == ArtistA && alias.Name == "Canonical name", "Exact artist aliases and string scores are supported");

        using var unavailableHandler = new ArtistHttpHandler(_ => throw new HttpRequestException("Offline"));
        using var unavailableClient = new HttpClient(unavailableHandler);
        var errorCache = new MemoryArtistStore();
        var unavailable = await new MusicBrainzArtistService(errorCache, unavailableClient).IdentifyAsync("Radiohead", [], default);
        Check(unavailable.Status == ArtistIdentityStatus.Unavailable && errorCache.Entries.Count == 0,
            "Network failures remain retryable and are not persisted as missing artists");

        using var retryHandler = new ArtistHttpHandler(_ => ArtistJson(ArtistSearch((ArtistA, "Radiohead"))));
        retryHandler.FirstResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryHandler.FirstResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        using var retryClient = new HttpClient(retryHandler);
        var retried = await new MusicBrainzArtistService(client: retryClient).IdentifyAsync("Radiohead", [], default);
        Check(retried.MusicBrainzId == ArtistA && retryHandler.Requests.Count == 2 &&
              retryHandler.Requests[1].At - retryHandler.Requests[0].At >= TimeSpan.FromSeconds(2),
            "429 retries respect Retry-After as well as the normal request limit");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await service.IdentifyAsync("Radiohead", [ArtistTrack(ArtistA)], cancellation.Token);
            throw new Exception("Cancelled identification unexpectedly succeeded");
        }
        catch (OperationCanceledException) { }

        await CheckArtistMatchingAsync();
        await CheckArtistCachePriorityAsync();
        await CheckArtistPersistenceAsync();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            artistIdentityService: service);
        vm.Tracks.Add(ArtistTrack(ArtistA));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await vm.ArtistIdentificationReady;
        Check(vm.Artists.Single().Identification.MusicBrainzId == ArtistA,
            "The library automatically exposes its resolved artist ID for photo providers");
        vm.Tracks[0] = ArtistTrack(ArtistB);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await vm.ArtistIdentificationReady;
        Check(vm.Artists.Single().Identification.MusicBrainzId == ArtistB, "Refreshing tags replaces the artist identity");
        var blocking = new BlockingArtistService();
        using var changingVm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            artistIdentityService: blocking);
        changingVm.Tracks.Add(ArtistTrack(ArtistA));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldGroup = changingVm.Artists.Single();
        var oldLookup = changingVm.ArtistIdentificationReady;
        Check(oldGroup.Identification.Result is null, "The UI remains responsive while identification is pending");
        changingVm.Tracks[0] = ArtistTrack(ArtistB);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await changingVm.ArtistIdentificationReady.WaitAsync(TimeSpan.FromSeconds(5));
        await oldLookup.WaitAsync(TimeSpan.FromSeconds(5));
        Check(blocking.Cancelled && oldGroup.Identification.Result is null && changingVm.Artists.Single().Identification.MusicBrainzId == ArtistB,
            "Library changes cancel obsolete lookups and cannot apply stale identities");
        Console.WriteLine("Artist identification tests passed.");
    }

    private static async Task CheckArtistPersistenceAsync()
    {
        using var directory = new TemporaryTestDirectory("MusicPlayerArtistTests");
        FixtureLibrary.Write(directory.Path);
        var path = Path.Combine(directory.Path, "01.wav");
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.MusicBrainzArtistId = ArtistA;
            file.Save();
        }
        var metadata = new TagLibMetadataService();
        var track = metadata.ReadTrack(path);
        Check(track.MusicBrainzArtistId == ArtistA && track.WithFileState(1, 2, true).MusicBrainzArtistId == ArtistA,
            "Real audio tags and file-state copies preserve the MusicBrainz artist ID");
        var database = Path.Combine(directory.Path, "music.db");
        MusicDbContext Context() => new(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={database}").Options);
        using (var db = Context())
        {
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().Single(m => m.EndsWith("_ReleaseTypes")));
            db.Database.ExecuteSqlRaw("INSERT INTO StorageStates (Key) VALUES ('LegacyJsonImported')");
            var key = Path.GetFullPath(path).ToUpperInvariant();
            db.Database.ExecuteSqlInterpolated($"INSERT INTO Tracks (PathKey, FilePath, Title, Artist, Album, DurationTicks, IsMissing, ExplicitlyAddedToLibrary, FileSize, LastWriteTimeUtcTicks) VALUES ({key}, {path}, {track.Title}, {track.Artist}, {track.Album}, {track.Duration.Ticks}, 0, 1, {track.FileSize}, {track.LastWriteTimeUtcTicks})");
        }
        var store = new SqliteMusicStore(database);
        var oldTrack = store.LoadLibrary().Single();
        Check(oldTrack.MetadataVersion == 0 && oldTrack.MusicBrainzArtistId is null, "Existing databases migrate without fabricating artist IDs");
        var refresh = new LibraryRefreshService(metadata);
        var updated = await refresh.RefreshAsync([oldTrack], [], false, default);
        Check(updated.Updates.Single().MusicBrainzArtistId == ArtistA,
            "An unchanged file is reread once after migration to recover embedded IDs");
        var upgradedTrack = updated.Updates.Single();
        upgradedTrack.ExplicitlyAddedToLibrary = true;
        store.SaveLibrary([upgradedTrack]);
        store.SavePlaylists([new Playlist([upgradedTrack]) { Name = "Artist test" }]);
        store.SaveSession(new PlaybackSession(upgradedTrack, TimeSpan.Zero, [upgradedTrack]));
        var restarted = new SqliteMusicStore(database);
        Check(restarted.LoadLibrary().Single().MusicBrainzArtistId == ArtistA && restarted.LoadPlaylists().Single().Tracks.Single().MusicBrainzArtistId == ArtistA &&
              restarted.LoadSession().CurrentTrack?.MusicBrainzArtistId == ArtistA,
            "Artist tag IDs survive library, playlist and session saves");
        Check((await refresh.RefreshAsync(restarted.LoadLibrary(), [], false, default)).Updates.Count == 0,
            "Upgraded unchanged files are not repeatedly scanned");
        var cached = new CachedArtistIdentity(new(ArtistIdentityStatus.Identified, ArtistB, "Artist"), DateTimeOffset.UtcNow.AddDays(1));
        store.SaveArtistIdentity("lookup", cached);
        Check(restarted.LoadArtistIdentity("lookup") == cached, "Artist lookup results persist independently of track tags");
        using (var db = Context()) Check(!db.Database.HasPendingModelChanges(), "Artist identity migration matches the EF model");
        var json = new JsonLibraryStore(Path.Combine(directory.Path, "roundtrip.json"));
        json.Save([upgradedTrack]);
        Check(json.Load().Single().MusicBrainzArtistId == ArtistA, "JSON storage preserves artist tag IDs");
        var jsonPlaylists = new JsonPlaylistStore(Path.Combine(directory.Path, "playlist-roundtrip.json"));
        jsonPlaylists.Save([new Playlist([upgradedTrack]) { Name = "Tagged" }]);
        Check(jsonPlaylists.Load().Single().Tracks.Single().MusicBrainzArtistId == ArtistA,
            "JSON playlists preserve artist tag IDs");
    }

    private sealed class MemoryArtistStore : IArtistIdentityStore
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, CachedArtistIdentity> Entries { get; } = new();
        public CachedArtistIdentity? LoadArtistIdentity(string key) => Entries.GetValueOrDefault(key);
        public void SaveArtistIdentity(string key, CachedArtistIdentity entry) => Entries[key] = entry;
    }

    private sealed class BlockingArtistService : IArtistIdentityService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<ArtistIdentity> IdentifyAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken cancellationToken)
        {
            if (tracks.Single().MusicBrainzArtistId == ArtistA)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new(ArtistIdentityStatus.Identified, ArtistB);
        }
    }

    private sealed class ArtistHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string UserAgent, TimeSpan At)> Requests { get; } = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public HttpResponseMessage? FirstResponse { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.UserAgent.ToString(), _clock.Elapsed));
            return Task.FromResult(Requests.Count == 1 && FirstResponse is { } first ? first : respond(request));
        }
    }
}
