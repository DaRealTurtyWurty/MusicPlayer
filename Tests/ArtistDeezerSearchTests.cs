using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicPlayer.Models;
using MusicPlayer.Services;

internal static partial class Program
{
    private static async Task CheckDeezerSearchAsync(string directory)
    {
        using var handler = new DeezerSearchHandler(CreateBrowserArtwork("Fanfan"));
        using var client = new HttpClient(handler);
        Track[] tracks = [new() { FilePath = "fool.flac", Artist = "Fanfan", Album = "Fool", Title = "Fool" }];
        var cache = Path.Combine(directory, "searched-deezer");
        var service = new ArtistPhotoService(cache, client);
        // Simulate an existing v3 cached miss with no name-search support.
        Directory.CreateDirectory(cache);
        var cacheFile = Path.Combine(cache, "commons-" + ArtistA + ".json");
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new { Attribution = (object?)null, Image = (byte[]?)null,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), ProviderRevision = 3 }));
        var photo = await service.LoadAsync(ArtistA, "Fanfan", tracks);
        Check(photo?.Attribution.SourceUrl == "https://www.deezer.com/artist/20" && handler.ArtistDownloads.SequenceEqual(["20"]) &&
              handler.AlbumsRead.Order().SequenceEqual(["10", "20"]),
            "An old miss refreshes and all exact-name candidates are checked against the library album and song");
        Check(JsonNode.Parse(File.ReadAllText(cacheFile))!["ContextKey"] is not null,
            "Name-verified photos persist the evidence context with the cached image");
        var count = handler.Requests.Count;
        handler.Offline = true;
        Check((await new ArtistPhotoService(cache, client).LoadAsync(ArtistA, " fanfan ", tracks))?.Attribution == photo!.Attribution && handler.Requests.Count == count,
            "A verified name-search photo survives restart offline");
        Track[] different = [new() { FilePath = "other.flac", Artist = "Fanfan", Album = "Other", Title = "Other" }];
        Check(await service.LoadAsync(ArtistA, "Fanfan", different) is null &&
              await new ArtistPhotoService(cache, client).LoadAsync(ArtistA, "Fanfan", different) is null,
            "Neither memory nor disk can leak a name-search photo into different song evidence");
        handler.Offline = false;
        handler.ArtistDownloads.Clear();
        async Task<ArtistPhoto?> Fresh() => await new ArtistPhotoService(Path.Combine(directory, Guid.NewGuid().ToString()), client)
            .LoadAsync(ArtistA, "Fanfan", tracks);
        handler.OtherMatches = true;
        Check(await Fresh() is null && handler.ArtistDownloads.Count == 0,
            "Two same-name artists with matching album/song evidence remain unresolved");
        handler.OtherMatches = false;
        handler.WrongTrack = true;
        Check(await Fresh() is null, "A matching artist and album without the exact song cannot select a portrait");
        handler.WrongTrack = false;
        handler.WrongCredit = true;
        Check(await Fresh() is null, "A song credited to a different Deezer artist cannot verify the candidate");
        handler.WrongCredit = false;
        handler.PartialAlbums = true;
        Check(await Fresh() is null, "An incomplete competing artist catalogue cannot imply uniqueness");
        handler.PartialAlbums = false;
        handler.PartialSearch = true;
        Check(await Fresh() is null, "An incomplete artist search cannot imply uniqueness");
        handler.PartialSearch = false;
        handler.PaginatedSearch = true;
        handler.Requests.Clear();
        Check(await Fresh() is not null && handler.Requests.Any(u => u.Query.Contains("index=100")),
            "Artist search pagination finds a matching candidate beyond the first page");
        handler.PaginatedSearch = false;
        handler.OtherMatches = true;
        handler.PartialTracks = true;
        Check(await Fresh() is null, "Incomplete competing album tracks cannot hide a second matching artist");
        handler.OtherMatches = false;
        handler.PartialTracks = false;
        handler.ApiError = true;
        var errorCache = Path.Combine(directory, "deezer-search-error");
        Check(await new ArtistPhotoService(errorCache, client).LoadAsync(ArtistA, "Fanfan", tracks) is null && !Directory.Exists(errorCache),
            "Catalogue API errors stay retryable rather than becoming cached missing artists");
        handler.ApiError = false;
        var contextCache = Path.Combine(directory, "deezer-search-context");
        var contextual = new ArtistPhotoService(contextCache, client);
        Check(await contextual.LoadAsync(ArtistA, "Fanfan", different) is null && await contextual.LoadAsync(ArtistA, "Fanfan", tracks) is not null,
            "A miss for one set of tags cannot block a later lookup with new evidence");
        var noContextCache = Path.Combine(directory, "deezer-search-no-context");
        var noContext = new ArtistPhotoService(noContextCache, client);
        handler.Requests.Clear();
        Check(await noContext.LoadAsync(ArtistA, "Fanfan") is null && !handler.Requests.Any(u => u.Host == "api.deezer.com"),
            "A name without album/song evidence never triggers a guessed Deezer match");
        Check(await noContext.LoadAsync(ArtistA, "Fanfan", tracks) is not null,
            "An earlier ID-only cache miss does not block a later contextual lookup");
        handler.Relations = """[{"url":{"resource":"https://www.deezer.com/artist/10"}}]""";
        handler.Requests.Clear();
        Check((await Fresh())?.Attribution.SourceUrl == "https://www.deezer.com/artist/10" && !handler.Requests.Any(u => u.AbsolutePath == "/search/artist"),
            "An explicit MusicBrainz Deezer link retains precedence over catalogue guessing");
        handler.Relations = """[{"url":{"resource":"https://www.deezer.com/artist/10"}},{"url":{"resource":"https://www.deezer.com/artist/20"}}]""";
        handler.Requests.Clear();
        Check(await Fresh() is null && !handler.Requests.Any(u => u.AbsolutePath == "/search/artist"),
            "Name searches cannot override conflicting MusicBrainz Deezer links");
        var previousSuccess = JsonNode.Parse(File.ReadAllText(cacheFile))!;
        previousSuccess["ProviderRevision"] = 3;
        previousSuccess.AsObject().Remove("ContextKey");
        File.WriteAllText(cacheFile, previousSuccess.ToJsonString());
        handler.Offline = true;
        count = handler.Requests.Count;
        Check((await new ArtistPhotoService(cache, client).LoadAsync(ArtistA, "Fanfan", tracks))?.Attribution == photo!.Attribution && handler.Requests.Count == count,
            "Unexpired successful pre-upgrade photos stay immediately available without new lookups");
        Console.WriteLine("Deezer name-search tests passed.");
    }

    private sealed class DeezerSearchHandler(byte[] image) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string> ArtistDownloads { get; } = [];
        public List<string> AlbumsRead { get; } = [];
        public string Relations { get; set; } = "[]";
        public bool Offline, OtherMatches, WrongTrack, WrongCredit, PartialAlbums, PartialSearch, PaginatedSearch, PartialTracks, ApiError;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (Offline) throw new HttpRequestException("Offline");
            if (uri.Host == "www.theaudiodb.com") return Task.FromResult(ArtistJson("""{"artists":null}"""));
            if (uri.Host == "musicbrainz.org") return Task.FromResult(ArtistJson("{\"relations\":" + Relations + "}"));
            if (uri.Host == "cdn-images.dzcdn.net") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            if (uri.Host != "api.deezer.com") throw new InvalidOperationException("Unexpected request " + uri);
            if (ApiError) return Task.FromResult(ArtistJson("""{"error":{"code":4}}"""));
            object data;
            if (uri.AbsolutePath == "/search/artist")
            {
                if (PaginatedSearch && !uri.Query.Contains("index=100"))
                    data = new { data = Enumerable.Range(100, 100).Select(i => new { id = i, name = "Other" }).ToArray(), total = 101, next = "https://evil.test/not-followed" };
                else if (PaginatedSearch) data = new { data = new[] { new { id = 20, name = "Fanfan" } }, total = 101 };
                else data = new { data = new[] { new { id = 10, name = "Fanfan" }, new { id = 20, name = "Fanfan" }, new { id = 30, name = "Fanfan tribute" } }, total = PartialSearch ? 1000 : 3 };
            }
            else if (uri.AbsolutePath.EndsWith("/albums", StringComparison.Ordinal))
            {
                var id = uri.AbsolutePath.Split('/')[2];
                AlbumsRead.Add(id);
                data = new { data = new[] { new { id = int.Parse(id) * 10, title = id == "20" || OtherMatches ? "Fool" : "Other" } }, total = PartialAlbums && id == "10" ? 500 : 1 };
            }
            else if (uri.AbsolutePath.StartsWith("/album/", StringComparison.Ordinal))
            {
                var id = int.Parse(uri.AbsolutePath.Split('/')[2]);
                var candidate = id / 10;
                data = new { id, title = "Fool", artist = new { id = candidate }, nb_tracks = PartialTracks && candidate == 10 ? 100 : 1,
                    tracks = new { data = new[] { new { title = WrongTrack || PartialTracks && candidate == 10 ? "Fool Remix" : "Fool", artist = new { id = WrongCredit ? 999 : candidate } } } } };
            }
            else if (uri.AbsolutePath.StartsWith("/artist/", StringComparison.Ordinal))
            {
                var id = uri.AbsolutePath.Split('/')[2];
                ArtistDownloads.Add(id);
                data = new { id = int.Parse(id), name = "Fanfan", type = "artist", picture_big = "https://cdn-images.dzcdn.net/images/artist/c437649ad5b69150b1c27314bc7744f7/500x500.jpg" };
            }
            else throw new InvalidOperationException("Unexpected request " + uri);
            return Task.FromResult(ArtistJson(JsonSerializer.Serialize(data)));
        }
    }
}
