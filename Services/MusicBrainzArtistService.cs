using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>Resolves identities, never artwork or changes to the user's audio files.</summary>
public sealed class MusicBrainzArtistService(IArtistIdentityStore? store = null, HttpClient? client = null) : IArtistIdentityService
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    // Shared across service instances, including retries. MusicBrainz requires <= 1 request/second.
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _nextRequest;
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private readonly Dictionary<string, CachedArtistIdentity> _cache = new(StringComparer.Ordinal);

    public async Task<ArtistIdentity> IdentifyAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = tracks.Select(t => MusicBrainzId.Normalize(t.MusicBrainzArtistId)).OfType<string>().Distinct().ToArray();
        if (ids.Length > 1) return new(ArtistIdentityStatus.Ambiguous, Source: ArtistIdentitySource.Tags);
        if (ids.Length == 1) return new(ArtistIdentityStatus.Identified, ids[0], artist, ArtistIdentitySource.Tags);
        if (string.IsNullOrWhiteSpace(artist) || Normalize(artist) is "UNKNOWN ARTIST" or "VARIOUS ARTISTS")
            return new(ArtistIdentityStatus.NotFound);
        // Wait for the existing library's one-time metadata upgrade before searching names.
        if (tracks.Any(t => t.MetadataVersion < 1 && !t.IsMissing)) return new(ArtistIdentityStatus.Unavailable);

        var albums = tracks.Select(t => t.Album).OfType<string>().Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray();
        // Context is part of the key: two artists with the same name must not share a cached match.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version = 1, Artist = Normalize(artist), Albums = albums }))));
        await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cache.TryGetValue(key, out var cached))
            {
                try { cached = store?.LoadArtistIdentity(key); }
                catch (Exception ex) { Trace.TraceWarning($"Could not read artist identity cache: {ex.Message}"); }
            }
            if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                _cache[key] = cached;
                return cached.Identity;
            }

            ArtistIdentity result;
            try { result = await SearchAsync(artist.Trim(), albums, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException)
            {
                Trace.TraceWarning($"MusicBrainz artist lookup unavailable: {ex.Message}");
                // A timeout or server error is not evidence that an artist does not exist.
                result = new(ArtistIdentityStatus.Unavailable);
            }
            var lifetime = result.Status switch
            {
                ArtistIdentityStatus.Identified => TimeSpan.FromDays(90),
                ArtistIdentityStatus.Unavailable => TimeSpan.FromMinutes(1),
                _ => TimeSpan.FromDays(7)
            };
            var entry = new CachedArtistIdentity(result, DateTimeOffset.UtcNow + lifetime);
            _cache[key] = entry;
            if (result.Status != ArtistIdentityStatus.Unavailable)
            {
                try { store?.SaveArtistIdentity(key, entry); }
                catch (Exception ex) { Trace.TraceWarning($"Could not save artist identity cache: {ex.Message}"); }
            }
            return result;
        }
        finally { _lookupGate.Release(); }
    }

    private async Task<ArtistIdentity> SearchAsync(string artist, string[] albums, CancellationToken token)
    {
        using var response = await GetAsync("artist", $"artist:{Quote(artist)}", token).ConfigureAwait(false);
        var root = response.RootElement;
        if (!root.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MusicBrainz returned no artist list.");
        var candidates = artists.EnumerateArray().Where(a => MatchesName(a, artist))
            .Select(a => new { Id = MusicBrainzId.Normalize(Text(a, "id")), Name = Text(a, "name"), Score = Score(a) })
            .Where(a => a.Id is not null).DistinctBy(a => a.Id).ToArray();
        // Do not infer uniqueness from an incomplete search response.
        var truncated = !root.TryGetProperty("count", out var count) || !count.TryGetInt32(out var total) || total > artists.GetArrayLength();
        if (truncated) return new(ArtistIdentityStatus.Ambiguous);
        if (candidates.Length == 1 && candidates[0].Score >= 95)
            return new(ArtistIdentityStatus.Identified, candidates[0].Id, candidates[0].Name);
        if (candidates.Length == 0) return new(ArtistIdentityStatus.NotFound);

        // For same-name artists, require an exact release title and matching artist credit.
        HashSet<string>? supported = null;
        foreach (var album in albums.Take(3))
        {
            using var releases = await GetAsync("release-group", $"releasegroup:{Quote(album)} AND artist:{Quote(artist)}", token)
                .ConfigureAwait(false);
            var releaseRoot = releases.RootElement;
            if (!releaseRoot.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("MusicBrainz returned no release-group list.");
            if (!releaseRoot.TryGetProperty("count", out var releaseCount) || !releaseCount.TryGetInt32(out var releaseTotal) ||
                releaseTotal > groups.GetArrayLength()) continue;
            var matches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateArray())
            {
                if (Normalize(Text(group, "title") ?? "") != album || !group.TryGetProperty("artist-credit", out var credits) ||
                    credits.ValueKind != JsonValueKind.Array) continue;
                foreach (var credit in credits.EnumerateArray())
                    if (credit.TryGetProperty("artist", out var creditedArtist) &&
                        (MatchesName(creditedArtist, artist) || Normalize(Text(credit, "name") ?? "") == Normalize(artist)) &&
                        MusicBrainzId.Normalize(Text(creditedArtist, "id")) is { } id && candidates.Any(c => c.Id == id))
                        matches.Add(id);
            }
            if (matches.Count == 0) continue;
            if (supported is null) supported = matches;
            else supported.IntersectWith(matches);
        }
        if (supported is { Count: 1 })
        {
            var match = candidates.Single(c => c.Id == supported.Single());
            return new(ArtistIdentityStatus.Identified, match.Id, match.Name);
        }
        return new(ArtistIdentityStatus.Ambiguous);
    }

    private async Task<JsonDocument> GetAsync(string entity, string query, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await RequestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var delay = _nextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1100);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://musicbrainz.org/ws/2/{entity}?query={Uri.EscapeDataString(query)}&fmt=json&limit=100");
                request.Headers.UserAgent.ParseAdd("MusicPlayer/1.0 (https://github.com/DaRealTurtyWurty/MusicPlayer)");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await (client ?? SharedClient).SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var retry = response.Headers.RetryAfter?.Date ??
                                DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5));
                    if (retry > _nextRequest) _nextRequest = retry;
                    if (attempt == 0) continue;
                }
                response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            }
            finally { RequestGate.Release(); }
        }
    }

    private static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static string Quote(string value) => "\"" + string.Concat(value.Select(c =>
        "+-!(){}[]^\"~*?:\\/&|".Contains(c) ? "\\" + c : c.ToString())) + "\"";
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static int Score(JsonElement artist) => artist.TryGetProperty("score", out var score) &&
        int.TryParse(score.ToString(), out var value) ? value : 0;
    private static bool MatchesName(JsonElement artist, string name) =>
        Normalize(Text(artist, "name") ?? "") == Normalize(name) ||
        (artist.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array &&
         aliases.EnumerateArray().Any(a => Normalize(Text(a, "name") ?? "") == Normalize(name)));
}
