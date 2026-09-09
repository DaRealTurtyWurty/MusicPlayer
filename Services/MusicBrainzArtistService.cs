using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>Resolves identities, never artwork or changes to the user's audio files.</summary>
public sealed class MusicBrainzArtistService(IArtistIdentityStore? store = null, HttpClient? client = null) : IArtistIdentityService
{
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedArtistIdentity> _cache = new(StringComparer.Ordinal);

    private static ArtistIdentity? TaggedIdentity(string artist, IReadOnlyList<Track> tracks)
    {
        var ids = tracks.Select(t => MusicBrainzId.Normalize(t.MusicBrainzArtistId)).OfType<string>().Distinct().ToArray();
        if (ids.Length > 1) return new(ArtistIdentityStatus.Ambiguous, Source: ArtistIdentitySource.Tags);
        if (ids.Length == 1) return new(ArtistIdentityStatus.Identified, ids[0], artist, ArtistIdentitySource.Tags);
        if (string.IsNullOrWhiteSpace(artist) || Normalize(artist) is "UNKNOWN ARTIST" or "VARIOUS ARTISTS")
            return new(ArtistIdentityStatus.NotFound);
        // Wait for the existing library's one-time metadata upgrade before searching names.
        if (tracks.Any(t => t.MetadataVersion < 1 && !t.IsMissing)) return new(ArtistIdentityStatus.Unavailable);
        return null;
    }

    private sealed record LookupContext(string Key, string LegacyKey, string[] Albums, string[] Titles);

    private static LookupContext Context(string artist, IReadOnlyList<Track> tracks)
    {
        var albums = tracks.Select(t => t.Album).OfType<string>().Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray();
        var titles = tracks.Select(t => t.Title).Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray();
        // Context is part of the key: two artists with the same name must not share a cached match.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version = 2, Artist = Normalize(artist), Albums = albums, Titles = titles }))));
        var legacyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version = 1, Artist = Normalize(artist), Albums = albums }))));
        return new(key, legacyKey, albums, titles);
    }

    public ArtistIdentity? GetKnownIdentity(string artist, IReadOnlyList<Track> tracks) =>
        TaggedIdentity(artist, tracks) ?? ReadKnownIdentity(Context(artist, tracks));

    private ArtistIdentity? ReadKnownIdentity(LookupContext context)
    {
        var cached = _cache.GetValueOrDefault(context.Key);
        if (cached is null)
        {
            cached = Load(context.Key);
            if (cached is not null) cached = _cache.GetOrAdd(context.Key, cached);
        }
        // Never roll back a newer result to an older version's answer, even if expired.
        if (cached is not null) return Usable(cached) ? cached.Identity : null;

        // The v2 upgrade improves failed matches; it need not discard a still-valid
        // successful v1 match for the exact same artist/album context.
        var legacy = Load(context.LegacyKey);
        if (legacy?.Identity.Status != ArtistIdentityStatus.Identified || !Usable(legacy)) return null;
        cached = _cache.GetOrAdd(context.Key, legacy);
        if (ReferenceEquals(cached, legacy)) Save(context.Key, legacy); // Preserve the original expiry.
        return Usable(cached) ? cached.Identity : null;
    }

    private static bool Usable(CachedArtistIdentity entry) => entry.ExpiresAt > DateTimeOffset.UtcNow &&
        Enum.IsDefined(entry.Identity.Status) && (entry.Identity.Status != ArtistIdentityStatus.Identified ||
            MusicBrainzId.Normalize(entry.Identity.MusicBrainzId) is not null);

    private CachedArtistIdentity? Load(string key)
    {
        try { return store?.LoadArtistIdentity(key); }
        catch (Exception ex) { Trace.TraceWarning($"Could not read artist identity cache: {ex.Message}"); return null; }
    }

    private void Save(string key, CachedArtistIdentity entry)
    {
        try { store?.SaveArtistIdentity(key, entry); }
        catch (Exception ex) { Trace.TraceWarning($"Could not save artist identity cache: {ex.Message}"); }
    }

    public async Task<ArtistIdentity> IdentifyAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TaggedIdentity(artist, tracks) is { } tagged) return tagged;
        var context = Context(artist, tracks);
        // Local hits must not queue behind a different artist's slow HTTP request.
        if (ReadKnownIdentity(context) is { } known) return known;
        await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadKnownIdentity(context) is { } cached) return cached;

            ArtistIdentity result;
            try { result = await SearchAsync(artist.Trim(), context.Albums, context.Titles, cancellationToken).ConfigureAwait(false); }
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
            _cache[context.Key] = entry;
            if (result.Status != ArtistIdentityStatus.Unavailable)
            {
                Save(context.Key, entry);
            }
            return result;
        }
        finally { _lookupGate.Release(); }
    }

    private sealed record Candidate(string Id, string? Name, int Score);

    private async Task<ArtistIdentity> SearchAsync(string artist, string[] albums, string[] titles, CancellationToken token)
    {
        using var response = await GetAsync("artist", $"(artist:{Quote(artist)} OR alias:{Quote(artist)})", token).ConfigureAwait(false);
        var root = response.RootElement;
        if (!root.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MusicBrainz returned no artist list.");
        var candidates = artists.EnumerateArray().Where(a => MatchesName(a, artist))
            .Where(a => MusicBrainzId.Normalize(Text(a, "id")) is not null)
            .Select(a => new Candidate(MusicBrainzId.Normalize(Text(a, "id"))!, Text(a, "name"), Score(a)))
            .DistinctBy(a => a.Id).ToDictionary(a => a.Id, StringComparer.Ordinal);
        // Do not infer uniqueness from an incomplete search response.
        var truncated = !Complete(root, artists);
        if (!truncated && candidates.Count == 1 && candidates.Values.Single().Score >= 95)
            return Identified(candidates.Values.Single());
        if (!truncated && candidates.Count == 0) return new(ArtistIdentityStatus.NotFound);

        // A broad/truncated artist search cannot prove uniqueness, but complete album
        // or recording searches can still identify the artist through their credits.
        HashSet<string>? supported = null;
        foreach (var album in albums.Take(3))
        {
            foreach (var title in TitleVariants(album, album: true))
            {
                var matches = await ContextMatchesAsync("release-group", "release-groups", "releasegroup", title, artist, candidates, token)
                    .ConfigureAwait(false);
                if (matches.Count == 0) continue;
                if (supported is null) supported = matches;
                else supported.IntersectWith(matches);
                break; // Prefer the full title when it supplies evidence.
            }
        }
        if (supported is { Count: 1 }) return Identified(candidates[supported.Single()]);
        // Conflicting albums must not be overridden by whichever track happens to come first.
        if (supported is { Count: 0 }) return new(ArtistIdentityStatus.Ambiguous);
        foreach (var title in titles.Take(3))
        {
            foreach (var variant in TitleVariants(title, album: false))
            {
                var matches = await ContextMatchesAsync("recording", "recordings", "recording", variant, artist, candidates, token)
                    .ConfigureAwait(false);
                if (matches.Count == 0) continue;
                if (supported is null) supported = matches;
                else supported.IntersectWith(matches);
                break;
            }
        }
        return supported is { Count: 1 } ? Identified(candidates[supported.Single()]) : new(ArtistIdentityStatus.Ambiguous);
    }

    private async Task<HashSet<string>> ContextMatchesAsync(string entity, string list, string titleField, string title,
        string artist, Dictionary<string, Candidate> candidates, CancellationToken token)
    {
        var artistTerms = new[] { $"artist:{Quote(artist)}", $"artistname:{Quote(artist)}" }
            .Concat(candidates.Keys.Take(20).Select(id => $"arid:{id}"));
        using var response = await GetAsync(entity, $"{titleField}:{Quote(title)} AND ({string.Join(" OR ", artistTerms)})", token)
            .ConfigureAwait(false);
        var root = response.RootElement;
        if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"MusicBrainz returned no {list} list.");
        var matches = new HashSet<string>(StringComparer.Ordinal);
        if (!Complete(root, items)) return matches;
        foreach (var item in items.EnumerateArray())
        {
            if (NormalizeTitle(Text(item, "title") ?? "") != title ||
                !item.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) continue;
            foreach (var credit in credits.EnumerateArray())
            {
                if (!credit.TryGetProperty("artist", out var creditedArtist) ||
                    MusicBrainzId.Normalize(Text(creditedArtist, "id")) is not { } id) continue;
                if (!candidates.ContainsKey(id) && !MatchesName(creditedArtist, artist) &&
                    Normalize(Text(credit, "name") ?? "") != Normalize(artist)) continue;
                candidates.TryAdd(id, new(id, Text(creditedArtist, "name"), 0));
                matches.Add(id);
            }
        }
        return matches;
    }

    private static ArtistIdentity Identified(Candidate candidate) => new(ArtistIdentityStatus.Identified, candidate.Id, candidate.Name);
    private static bool Complete(JsonElement root, JsonElement items) => root.TryGetProperty("count", out var count) &&
        count.TryGetInt32(out var total) && total <= items.GetArrayLength();

    private static string NormalizeTitle(string value) => Regex.Replace(Normalize(value).Replace('’', '\'').Replace('‘', '\''),
        @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static IEnumerable<string> TitleVariants(string value, bool album)
    {
        var exact = NormalizeTitle(value);
        yield return exact;
        // Only remove recognized trailing metadata, not arbitrary subtitles, numbers,
        // remixes, live versions, or Taylor's Version distinctions.
        var annotations = album
            ? @"(?:FEAT\.?|FT\.?|FEATURING)\s+[^)\]]+|(?:DELUXE|EXPANDED|SPECIAL|ANNIVERSARY)(?:\s+(?:EDITION|VERSION))?(?:\s*-\s*(?:EXPLICIT|CLEAN))?|EXPLICIT|CLEAN"
            : @"(?:FEAT\.?|FT\.?|FEATURING)\s+[^)\]]+";
        var stripped = Regex.Replace(exact, @"(?:\s*[\(\[](?:(?:" + annotations + @"))[\)\]])+$", "",
            RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        if (stripped.Length > 0 && stripped != exact) yield return stripped;
    }

    private Task<JsonDocument> GetAsync(string entity, string query, CancellationToken token) =>
        MusicMetadataHttp.JsonAsync(client ?? MusicMetadataHttp.Client,
            new Uri($"https://musicbrainz.org/ws/2/{entity}?query={Uri.EscapeDataString(query)}&fmt=json&limit=100"), token);

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
