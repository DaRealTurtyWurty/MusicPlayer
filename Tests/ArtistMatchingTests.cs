using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicPlayer.Models;
using MusicPlayer.Services;

internal static partial class Program
{
    private static string ArtistEvidence(string list, params (string Title, string Id, string Name)[] rows) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["count"] = rows.Length,
            [list] = rows.Select(r => new { title = r.Title,
                credits = new[] { new { artist = new { id = r.Id, name = r.Name } } } })
                .Select(r => new Dictionary<string, object> { ["title"] = r.title, ["artist-credit"] = r.credits })
        });

    private static Track MatchingTrack(string album, string title) => new()
        { FilePath = title + ".wav", Title = title, Album = album };

    private static async Task CheckArtistMatchingAsync()
    {
        async Task<ArtistIdentity> Resolve(string artist, Track[] tracks, Func<Uri, string> response, MemoryArtistStore? cache = null)
        {
            using var handler = new ArtistHttpHandler(r => ArtistJson(response(r.RequestUri!)));
            using var client = new HttpClient(handler);
            return await new MusicBrainzArtistService(cache, client).IdentifyAsync(artist, tracks, default);
        }
        var alias = await Resolve("Kanye West", [], uri =>
        {
            Check(Uri.UnescapeDataString(uri.Query).Contains("alias:\"Kanye West\""), "Artist searches explicitly include aliases");
            return $$$"""{"count":1,"artists":[{"id":"{{{ArtistA}}}","name":"Ye","score":100,"aliases":[{"name":"Kanye West"}]}]}""";
        });
        Check(alias.MusicBrainzId == ArtistA && alias.Name == "Ye", "Former artist names resolve to the canonical artist");

        var broad = $$$"""{"count":2527,"artists":[{"id":"{{{ArtistA}}}","name":"ROSÉ","score":83,"aliases":[{"name":"Rose"}]}]}""";
        string RoseResponse(Uri uri) => uri.AbsolutePath.EndsWith("artist") ? broad
            : ArtistEvidence("release-groups", ("rosie", ArtistA, "ROSÉ"));
        Check((await Resolve("ROSÉ", [MatchingTrack("rosie", "number one girl")], RoseResponse)).MusicBrainzId == ArtistA,
            "Album evidence resolves ROSÉ even when the initial artist search is truncated and low-scoring");
        Check((await Resolve("Rose", [MatchingTrack("rosie", "APT.")], RoseResponse)).MusicBrainzId == ArtistA,
            "Known candidate IDs preserve alias matches when release credits use the canonical name");
        Check((await Resolve("Rose", [], RoseResponse)).Status == ArtistIdentityStatus.Ambiguous,
            "A truncated artist search without context still cannot imply uniqueness");

        var queries = new List<string>();
        var blaire = await Resolve("Blaire", [MatchingTrack("Forever (feat. TJ Brown)", "Forever")], uri =>
        {
            queries.Add(Uri.UnescapeDataString(uri.Query));
            if (uri.AbsolutePath.EndsWith("artist")) return ArtistSearch((ArtistA, "Blaire"), (ArtistB, "Blaire"));
            return queries[^1].Contains("FEAT") ? ArtistEvidence("release-groups")
                : ArtistEvidence("release-groups", ("Forever", ArtistA, "Blaire"));
        });
        Check(blaire.MusicBrainzId == ArtistA && queries.Count == 3,
            "Featured-artist suffixes are retried as the base album title after the full title misses");

        var bennyQueries = new List<string>();
        string BennyResponse(Uri uri)
        {
            bennyQueries.Add(Uri.UnescapeDataString(uri.Query));
            return uri.AbsolutePath.Split('/').Last() switch
            {
                "artist" => ArtistSearch((ArtistA, "benny blanco"), (ArtistB, "Benny Blanco")),
                "release-group" => ArtistEvidence("release-groups"),
                _ => ArtistEvidence("recordings", ("Eastside", ArtistA, "benny blanco"))
            };
        }
        Check((await Resolve("Benny Blanco", [MatchingTrack("FRIENDS KEEP SECRETS 2", "Eastside")], BennyResponse)).MusicBrainzId == ArtistA,
            "Exact recording credits disambiguate an artist when the tagged album is absent from MusicBrainz");
        Check(bennyQueries.Count == 3 && bennyQueries[1].Contains("FRIENDS KEEP SECRETS 2"),
            "Meaningful album numbers are not stripped to force a match");

        string ConflictingResponse(Uri uri) => uri.AbsolutePath.EndsWith("artist")
            ? ArtistSearch((ArtistA, "Same Name"), (ArtistB, "Same Name"))
            : Uri.UnescapeDataString(uri.Query).Contains("FIRST")
                ? ArtistEvidence("release-groups", ("First", ArtistA, "Same Name"))
                : ArtistEvidence("release-groups", ("Second", ArtistB, "Same Name"));
        Check((await Resolve("Same Name", [MatchingTrack("First", "Song"), MatchingTrack("Second", "Song")], ConflictingResponse)).Status == ArtistIdentityStatus.Ambiguous,
            "Conflicting album evidence remains ambiguous instead of choosing the first artist");
        Check((await Resolve("Same Name", [MatchingTrack("Missing", "Song")], uri => uri.AbsolutePath.Split('/').Last() switch
        {
            "artist" => ArtistSearch((ArtistA, "Same Name"), (ArtistB, "Same Name")),
            "release-group" => ArtistEvidence("release-groups"),
            _ => ArtistEvidence("recordings", ("Song (remix)", ArtistA, "Same Name"))
        })).Status == ArtistIdentityStatus.Ambiguous, "A similar song title cannot prove an artist match");
        Check((await Resolve("Same Name", [MatchingTrack("First", "")], uri => uri.AbsolutePath.EndsWith("artist")
            ? ArtistSearch((ArtistA, "Same Name"), (ArtistB, "Same Name"))
            : ArtistEvidence("release-groups", ("First", ArtistA, "Same Name")).Replace("\"count\":1", "\"count\":101")))
            .Status == ArtistIdentityStatus.Ambiguous, "Truncated album evidence cannot falsely prove uniqueness");

        foreach (var title in new[] { "Red (Taylor's Version)", "Album (Live)", "Album (Remix)" })
        {
            var titleQueries = new List<string>();
            await Resolve("Same Name", [MatchingTrack(title, "")], uri =>
            {
                titleQueries.Add(Uri.UnescapeDataString(uri.Query));
                return uri.AbsolutePath.EndsWith("artist") ? ArtistSearch((ArtistA, "Same Name"), (ArtistB, "Same Name"))
                    : ArtistEvidence("release-groups");
            });
            Check(titleQueries.Count == 2, $"Meaningful version metadata stays intact: {title}");
        }
        Check((await Resolve("Same Name", [MatchingTrack("Album (deluxe version - explicit) (deluxe version)", "")], uri =>
            uri.AbsolutePath.EndsWith("artist") ? ArtistSearch((ArtistA, "Same Name"), (ArtistB, "Same Name"))
                : ArtistEvidence("release-groups", ("Album", ArtistA, "Same Name")))).MusicBrainzId == ArtistA,
            "Repeated recognized edition annotations can be removed without changing the album name");

        foreach (var status in new[] { ArtistIdentityStatus.NotFound, ArtistIdentityStatus.Ambiguous })
        {
            var cache = new MemoryArtistStore();
            var oldKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { Version = 1, Artist = "KANYE WEST", Albums = Array.Empty<string>() }))));
            cache.Entries[oldKey] = new(new(status), DateTimeOffset.UtcNow.AddDays(7));
            var result = await Resolve("Kanye West", [], _ => ArtistSearch((ArtistA, "Kanye West")), cache);
            Check(result.MusicBrainzId == ArtistA && cache.Entries.Count == 2, $"Legacy {status} cache entries do not block the improved lookup");
        }
        var trackCache = new MemoryArtistStore();
        await Resolve("Benny Blanco", [MatchingTrack("Missing", "Eastside")], BennyResponse, trackCache);
        var before = bennyQueries.Count;
        await Resolve("Benny Blanco", [MatchingTrack("Missing", "Another song")], BennyResponse, trackCache);
        Check(bennyQueries.Count > before, "Track evidence is included in the identity cache key");
        Console.WriteLine("Artist matching regression tests passed.");
    }

    private static async Task CheckArtistMatchingLiveAsync()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var service = new MusicBrainzArtistService();
        var failures = new List<string>();
        foreach (var (name, album, title, expected) in new[]
                 {
                     ("Kanye West", "JESUS IS KING", "God Is", "164f0d73-1234-4e2c-8743-d77bf2191051"),
                     ("Benny Blanco", "FRIENDS KEEP SECRETS 2", "Eastside", "5a6131f9-f8db-4722-b4d7-6a0c7183c590"),
                     ("Blaire", "Forever (feat. TJ Brown)", "Forever", "9da2aa77-0e55-4140-a6da-bb25f4190002"),
                     ("ROSÉ", "rosie", "number one girl", "7f233cda-eacb-4235-b681-5f7be343a1a2"),
                     ("Rose", "rosie", "APT.", "7f233cda-eacb-4235-b681-5f7be343a1a2"),
                     ("Taylor Swift", "1989 (Taylor's Version)", "Style (Taylor's Version)", "20244d07-534f-4eff-b4d4-930878889970")
                 })
        {
            var identity = await service.IdentifyAsync(name, [MatchingTrack(album, title)], default);
            if (identity.MusicBrainzId == expected)
                Check(true, $"Live artist match: {name} -> {identity.Name} ({identity.Status})");
            else
            {
                failures.Add($"{name}: {identity.Status}");
                Console.WriteLine($"FAIL: Live artist match: {name} -> {identity.Name} ({identity.Status})");
            }
        }
        Check(failures.Count == 0, "All live artist matching cases resolve: " + string.Join(", ", failures));
    }
}
