using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private const string CoverRelease = "76df3287-6cda-33eb-8e9a-044b5e15ffdd";
    private const string CoverGroup = "70664047-2545-4e46-b75f-4556f2a7b83e";
    private static string CoverUrl(string entity, string id) => $"https://coverartarchive.org/{entity}/{id}/front-500";
    private static Track CoverTrack(string? release = null, string? group = null, string album = "Album", string artist = "Album Artist") => new()
    {
        FilePath = "private/local/song.flac", Title = "Song", Artist = "Guest singer", AlbumArtist = artist,
        Album = album, MusicBrainzReleaseId = release, MusicBrainzReleaseGroupId = group, ArtworkData = [1, 2, 3]
    };

    private static async Task CheckAlbumArtworkAsync()
    {
        using var directory = new TemporaryTestDirectory("MusicPlayerCoverTests");
        var clock = new PresenceClock();
        using var handler = new CoverHttpHandler(_ => CoverJson("{\"images\":[{\"front\":true}]}"));
        using var http = new HttpClient(handler);
        string Cache(string name) => Path.Combine(directory.Path, name);
        var resolver = new AlbumArtworkUrlResolver(http, Cache("hits"), clock);
        var tagged = CoverTrack(CoverRelease, CoverGroup);
        var results = await Task.WhenAll(resolver.ResolveAsync(tagged, default), resolver.ResolveAsync(tagged, default));
        Check(results.All(url => url == CoverUrl("release", CoverRelease)) && handler.Requests.Count == 1,
            "Tagged releases use CAA directly and simultaneous requests share the cache");
        Check(handler.Requests.All(u => !u.ToString().Contains("private") && !u.ToString().Contains("Guest")),
            "Cover requests never send file paths, embedded artwork or unnecessary track metadata");
        resolver = new(http, Cache("hits"), clock);
        Check(await resolver.ResolveAsync(tagged, default) == results[0] && handler.Requests.Count == 1,
            "Cover URLs survive an app restart without network requests");
        clock.Advance(TimeSpan.FromDays(31));
        await resolver.ResolveAsync(tagged, default);
        Check(handler.Requests.Count == 2, "Successful cover entries expire after thirty days");

        handler.Reply = uri => uri.AbsolutePath.StartsWith("/release/", StringComparison.Ordinal)
            ? new(HttpStatusCode.NotFound) : CoverJson("{\"images\":[{\"front\":true}]}");
        resolver = new(http, Cache("fallback"), clock);
        Check(await resolver.ResolveAsync(tagged, default) == CoverUrl("release-group", CoverGroup),
            "A release with no cover falls back to its tagged release group");
        handler.Reply = uri => uri.Host == "musicbrainz.org"
            ? CoverJson(JsonSerializer.Serialize(new Dictionary<string, object> { ["release-group"] = new { id = CoverGroup } }))
            : uri.AbsolutePath.StartsWith("/release/", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound)
            : CoverJson("{\"images\":[{\"front\":true}]}");
        Check(await new AlbumArtworkUrlResolver(http, Cache("derived"), clock).ResolveAsync(CoverTrack(CoverRelease), default)
              == CoverUrl("release-group", CoverGroup), "Missing group tags can be recovered from the authoritative release");

        handler.Reply = uri => uri.Host == "musicbrainz.org" ? CoverJson(SearchGroups(CoverGroup))
            : CoverJson("{\"images\":[{\"front\":true}]}");
        resolver = new(http, Cache("search"), clock);
        Check(await resolver.ResolveAsync(CoverTrack(), default) == CoverUrl("release-group", CoverGroup),
            "Untagged albums accept one exact album-title and album-artist match");
        var search = Uri.UnescapeDataString(handler.Requests.Last(u => u.Host == "musicbrainz.org").Query);
        Check(search.Contains("artist:\"Album Artist\"") && !search.Contains("Guest singer"),
            "Album search uses album artist instead of a compilation track's guest performer");
        var before = handler.Requests.Count;
        await resolver.ResolveAsync(CoverTrack(album: " ALBUM ", artist: "album  artist"), default);
        Check(handler.Requests.Count == before, "Album cache keys normalize case and whitespace across tracks");
        handler.Reply = _ => CoverJson(SearchGroups(CoverGroup, CoverRelease));
        Check(await new AlbumArtworkUrlResolver(http, Cache("ambiguous"), clock).ResolveAsync(CoverTrack(), default) is null,
            "Two exact album matches are rejected instead of selecting arbitrary artwork");
        handler.Reply = _ => CoverJson(SearchGroups(CoverGroup).Replace("\"count\":1", "\"count\":26"));
        Check(await new AlbumArtworkUrlResolver(http, Cache("truncated"), clock).ResolveAsync(CoverTrack(), default) is null,
            "Truncated search results cannot establish a unique album match");
        handler.Reply = _ => CoverJson(SearchGroups(CoverGroup).Replace("Album Artist", "Wrong Artist"));
        Check(await new AlbumArtworkUrlResolver(http, Cache("wrong"), clock).ResolveAsync(CoverTrack(), default) is null,
            "A same-title album by a different artist cannot supply a cover");
        handler.Reply = _ => CoverJson("{\"images\":[{\"front\":false}]}");
        resolver = new(http, Cache("missing"), clock);
        Check(await resolver.ResolveAsync(CoverTrack(group: CoverGroup), default) is null, "Back-cover-only entries remain text-only");
        before = handler.Requests.Count;
        await new AlbumArtworkUrlResolver(http, Cache("missing"), clock).ResolveAsync(CoverTrack(group: CoverGroup), default);
        Check(handler.Requests.Count == before, "Confirmed missing covers are cached across restart");
        clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromSeconds(1)));
        await resolver.ResolveAsync(CoverTrack(group: CoverGroup), default);
        Check(handler.Requests.Count == before + 1, "Confirmed misses expire after one day");

        handler.Reply = _ => new(HttpStatusCode.BadGateway);
        resolver = new(http, Cache("outage"), clock);
        await resolver.ResolveAsync(CoverTrack(group: CoverGroup), default);
        before = handler.Requests.Count;
        await resolver.ResolveAsync(CoverTrack(group: CoverGroup), default);
        Check(handler.Requests.Count == before && !Directory.Exists(Cache("outage")),
            "Outages use a short in-memory backoff without persisting a false missing cover");
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        handler.Reply = _ => CoverJson("{\"images\":[{\"front\":true}]}");
        Check(await resolver.ResolveAsync(CoverTrack(group: CoverGroup), default) is not null, "A failed cover lookup recovers after its short retry delay");
        foreach (var file in Directory.GetFiles(Cache("hits"))) File.WriteAllText(file, "not json");
        Check(await new AlbumArtworkUrlResolver(http, Cache("hits"), clock).ResolveAsync(tagged, default) is not null,
            "Corrupt disk cache entries are safely replaced");
        var token = new CancellationToken(true);
        try { await resolver.ResolveAsync(tagged, token); Check(false, "Cancellation must propagate"); }
        catch (OperationCanceledException) { Check(true, "Cancelled lookups cannot write a missing-cover cache entry"); }
        before = handler.Requests.Count;
        Check(await resolver.ResolveAsync(Track("No album"), default) is null && handler.Requests.Count == before,
            "Tracks without album identity cause no network requests");
        Check(!AlbumArtworkUrlResolver.IsCoverUrl("https://localhost/cover.png") &&
              !AlbumArtworkUrlResolver.IsCoverUrl(CoverUrl("release", CoverRelease) + "?redirect=bad"),
            "Persisted cover URLs are restricted to canonical public CAA endpoints");
        var activity = DiscordPresenceClient.CreateActivity(new("Song", "Artist", null, null, results[0], "Album"));
        Check(activity.Assets.LargeImageKey == results[0] && activity.Assets.LargeImageText == "Album" &&
              activity.Assets.LargeImageUrl is null, "Discord artwork is sent as the image key, with album tooltip");
        await CheckAlbumArtworkPersistenceAsync(directory.Path);
    }

    private static string SearchGroups(params string[] ids) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["count"] = ids.Length,
        ["release-groups"] = ids.Select(id => new Dictionary<string, object>
        {
            ["id"] = id, ["title"] = "Album",
            ["artist-credit"] = new[] { new { name = "Album Artist", artist = new { name = "Album Artist" } } }
        })
    });
    private static HttpResponseMessage CoverJson(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class CoverHttpHandler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public Func<Uri, HttpResponseMessage> Reply { get; set; } = reply;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get && request.Headers.UserAgent.ToString().Contains("MusicPlayer"),
                "Cover metadata requests identify the app and use read-only HTTP");
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Reply(request.RequestUri!));
        }
    }

    private static async Task CheckAlbumArtworkPersistenceAsync(string directory)
    {
        FixtureLibrary.Write(directory);
        var path = Path.Combine(directory, "01.wav");
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.AlbumArtists = ["Album Artist"];
            file.Tag.MusicBrainzReleaseId = CoverRelease;
            file.Tag.MusicBrainzReleaseGroupId = CoverGroup;
            file.Save();
        }
        var metadata = new TagLibMetadataService();
        var track = metadata.ReadTrack(path);
        bool HasTags(Track t) => t.AlbumArtist == "Album Artist" && t.MusicBrainzReleaseId == CoverRelease &&
            t.MusicBrainzReleaseGroupId == CoverGroup && t.MetadataVersion == MusicPlayer.Models.Track.CurrentMetadataVersion;
        Check(HasTags(track) && HasTags(track.WithFileState(1, 2, true)), "Actual audio tags and file-state copies retain release identity");
        var database = Path.Combine(directory, "covers.db");
        using (var db = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={database}").Options))
        {
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().Single(m => m.EndsWith("_ArtistIdentities")));
            db.Database.ExecuteSqlRaw("INSERT INTO StorageStates (Key) VALUES ('LegacyJsonImported')");
            var key = Path.GetFullPath(path).ToUpperInvariant();
            db.Database.ExecuteSqlInterpolated($"INSERT INTO Tracks (PathKey, FilePath, Title, DurationTicks, IsMissing, ExplicitlyAddedToLibrary, MetadataVersion, FileSize, LastWriteTimeUtcTicks) VALUES ({key}, {path}, {track.Title}, {track.Duration.Ticks}, 0, 1, 1, {track.FileSize}, {track.LastWriteTimeUtcTicks})");
        }
        var store = new SqliteMusicStore(database);
        var old = store.LoadLibrary().Single();
        Check(old.MetadataVersion == 1 && old.MusicBrainzReleaseId is null, "Existing SQLite libraries migrate without inventing release IDs");
        var refresh = new LibraryRefreshService(metadata);
        var updated = (await refresh.RefreshAsync([old], [], false, default)).Updates.Single();
        Check(HasTags(updated), "Unchanged version-one metadata is refreshed once to recover release tags");
        store.SaveLibrary([updated]);
        store.SavePlaylists([new Playlist([updated]) { Name = "Covers" }]);
        store.SaveSession(new PlaybackSession(updated, TimeSpan.Zero, [updated]));
        store = new(database);
        Check(HasTags(store.LoadLibrary().Single()) && HasTags(store.LoadPlaylists().Single().Tracks.Single()) &&
              HasTags(store.LoadSession().CurrentTrack!), "Release identity survives SQLite library, playlist and session reloads");
        Check((await refresh.RefreshAsync(store.LoadLibrary(), [], false, default)).Updates.Count == 0,
            "Current release metadata does not trigger repeated rescans");
        using (var db = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={database}").Options))
            Check(!db.Database.HasPendingModelChanges(), "Album artwork migration matches the EF model");
        var library = new JsonLibraryStore(Path.Combine(directory, "roundtrip-library.json"));
        library.Save([track]);
        var playlists = new JsonPlaylistStore(Path.Combine(directory, "roundtrip-playlists.json"));
        playlists.Save([new Playlist([track]) { Name = "Covers" }]);
        Check(HasTags(library.Load().Single()) && HasTags(playlists.Load().Single().Tracks.Single()),
            "JSON library and playlist storage preserve release metadata");
    }

    private static async Task CheckDiscordArtworkBindingAsync()
    {
        var clock = new PresenceClock();
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        vm.ConfigureDiscordPresence(new(true));
        var client = new FakeDiscordClient("test") { ConnectOnPump = true };
        var resolver = new DeferredCoverResolver();
        using var binding = new DiscordPresenceBinding(vm, Dispatcher.CurrentDispatcher, _ => client, clock, resolver);
        async Task Drain() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline) { await Task.Delay(25); await Drain(); }
            Check(condition(), "Artwork background work reaches the dispatcher");
        }
        vm.SelectedTrack = CoverTrack(CoverRelease);
        vm.PlaySelectedTrackCommand.Execute(null);
        await WaitFor(() => resolver.Requests.Count == 1 && client.Desired is not null);
        Check(client.Desired!.ArtworkUrl is null, "Text presence publishes before a slow cover lookup completes");
        var timestamps = (client.Desired.Start, client.Desired.End);
        clock.Advance(TimeSpan.FromSeconds(5));
        resolver.Requests[0].Result.SetResult(CoverUrl("release", CoverRelease));
        await WaitFor(() => client.Desired?.ArtworkUrl is not null);
        Check((client.Desired!.Start, client.Desired.End) == timestamps, "Artwork arrival does not restart the playback timer");
        vm.SelectedTrack = CoverTrack(group: CoverGroup);
        vm.PlaySelectedTrackCommand.Execute(null);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitFor(() => resolver.Requests.Count == 2 && client.Desired?.ArtworkUrl is null);
        vm.SelectedTrack = Track("Next track without album");
        vm.PlaySelectedTrackCommand.Execute(null);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitFor(() => resolver.Requests.Count == 3);
        Check(resolver.Requests[1].Token.IsCancellationRequested, "Changing tracks cancels the previous artwork request");
        resolver.Requests[1].Result.SetResult(CoverUrl("release-group", CoverGroup)); // Deliberately ignore cancellation.
        resolver.Requests[2].Result.SetResult(null);
        await Task.Delay(100);
        await Drain();
        Check(client.Desired is { Title: "Next track without album", ArtworkUrl: null }, "Late results cannot replace the current track's artwork");
        vm.SelectedTrack = CoverTrack(CoverRelease);
        vm.PlaySelectedTrackCommand.Execute(null);
        await WaitFor(() => resolver.Requests.Count == 4);
        vm.PauseCommand.Execute(null);
        await Drain();
        resolver.Requests[3].Result.SetResult(CoverUrl("release", CoverRelease));
        await Task.Delay(100);
        Check(client.Desired is null && resolver.Requests[3].Token.IsCancellationRequested, "Pause cancels lookup and late artwork cannot revive presence");
        vm.ConfigureDiscordPresence(new(true, LookupAlbumCovers: false));
        vm.PlayCommand.Execute(null);
        await Drain();
        await Task.Delay(100);
        Check(resolver.Requests.Count == 4 && client.Desired?.ArtworkUrl is null, "Disabling online covers retains text sharing without new lookups");
    }

    private sealed class DeferredCoverResolver : IAlbumArtworkUrlResolver
    {
        private readonly List<(TaskCompletionSource<string?> Result, CancellationToken Token)> _requests = [];
        public IReadOnlyList<(TaskCompletionSource<string?> Result, CancellationToken Token)> Requests
        { get { lock (_requests) return _requests.ToArray(); } }
        public Task<string?> ResolveAsync(Track track, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requests) _requests.Add((result, cancellationToken));
            return result.Task;
        }
    }
}
