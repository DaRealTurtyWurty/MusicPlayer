using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>Resolves public covers without uploading embedded images or local paths.</summary>
public sealed class AlbumArtworkUrlResolver : IAlbumArtworkUrlResolver
{
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _memory = new();
    private sealed record CacheEntry(string Key, string? Url, DateTimeOffset ExpiresAt, int Version = 1);

    public AlbumArtworkUrlResolver(HttpClient? http = null, string? cacheDirectory = null, TimeProvider? clock = null)
    {
        _http = http ?? MusicMetadataHttp.Client;
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicPlayer", "cache", "album-covers");
        _clock = clock ?? TimeProvider.System;
    }

    internal static string? LookupKey(Track track)
    {
        var release = MusicBrainzId.Normalize(track.MusicBrainzReleaseId);
        var group = MusicBrainzId.Normalize(track.MusicBrainzReleaseGroupId);
        if (release is not null) return $"release:{release}:{group}";
        if (group is not null) return $"group:{group}";
        var album = Normalize(track.Album);
        var artist = Normalize(AlbumArtist(track));
        return album.Length is > 0 and <= 512 && artist.Length is > 0 and <= 512
            ? JsonSerializer.Serialize(new[] { "search-v1", album, artist }) : null;
    }

    private static string? AlbumArtist(Track track) =>
        string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;

    private static string Normalize(string? text) => Regex.Replace((text ?? "").Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").ToUpperInvariant();

    public async Task<string?> ResolveAsync(Track track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = LookupKey(track);
        if (key is null) return null;
        // Serialize lookups and recheck the cache: simultaneous requests for songs on
        // the same album share one result, without sharing a caller's cancellation.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (_memory.TryGetValue(key, out var cached) && cached.ExpiresAt > now) return cached.Url;
            var path = Path.Combine(_cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
            cached = await ReadCacheAsync(path, key, now, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                Remember(cached);
                return cached.Url;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var url = await LookupAsync(track, timeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new CacheEntry(key, url, _clock.GetUtcNow().Add(url is null ? TimeSpan.FromDays(1) : TimeSpan.FromDays(30)));
                Remember(entry);
                await WriteCacheAsync(path, entry, cancellationToken).ConfigureAwait(false);
                return url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
            {
                // Outages, throttling and malformed responses are not confirmed misses.
                // A short memory-only delay prevents rapid track changes hammering an outage.
                Trace.TraceWarning($"Album cover lookup unavailable: {ex.Message}");
                Remember(new(key, null, _clock.GetUtcNow().AddMinutes(1)));
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    private void Remember(CacheEntry entry)
    {
        if (_memory.Count >= 512) _memory.Remove(_memory.MinBy(p => p.Value.ExpiresAt).Key);
        _memory[entry.Key] = entry;
    }

    private async Task<string?> LookupAsync(Track track, CancellationToken token)
    {
        var release = MusicBrainzId.Normalize(track.MusicBrainzReleaseId);
        var group = MusicBrainzId.Normalize(track.MusicBrainzReleaseGroupId);
        if (release is not null)
        {
            var cover = await CoverAsync("release", release, token).ConfigureAwait(false);
            if (cover is not null) return cover;
            if (group is null)
            {
                using var document = await GetAsync($"https://musicbrainz.org/ws/2/release/{release}?inc=release-groups&fmt=json", token).ConfigureAwait(false);
                if (document is not null && document.RootElement.TryGetProperty("release-group", out var releaseGroup))
                    group = MusicBrainzId.Normalize(Text(releaseGroup, "id"));
            }
            // An explicit release ID is authoritative: don't replace it with a name guess.
            return group is null ? null : await CoverAsync("release-group", group, token).ConfigureAwait(false);
        }
        if (group is not null) return await CoverAsync("release-group", group, token).ConfigureAwait(false);

        var query = $"releasegroup:{Quote(track.Album!)} AND artist:{Quote(AlbumArtist(track)!)}";
        using var search = await GetAsync("https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=25&query=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        if (search is null) throw new InvalidDataException("Album search endpoint was unavailable.");
        var root = search.RootElement;
        var groups = root.GetProperty("release-groups");
        if (groups.ValueKind != JsonValueKind.Array || !root.GetProperty("count").TryGetInt32(out var count))
            throw new InvalidDataException("Invalid album search response.");
        // A truncated result page cannot prove that a matching album is unique.
        if (count > groups.GetArrayLength()) return null;
        var matches = groups.EnumerateArray().Where(g => Normalize(Text(g, "title")) == Normalize(track.Album) &&
            Normalize(Credit(g)) == Normalize(AlbumArtist(track)))
            .Select(g => MusicBrainzId.Normalize(Text(g, "id"))).Where(id => id is not null).Distinct().ToArray();
        return matches.Length == 1 ? await CoverAsync("release-group", matches[0]!, token).ConfigureAwait(false) : null;
    }

    private async Task<string?> CoverAsync(string entity, string id, CancellationToken token)
    {
        var rootUrl = $"https://coverartarchive.org/{entity}/{id}";
        using var document = await GetAsync(rootUrl, token).ConfigureAwait(false);
        if (document is null) return null;
        var images = document.RootElement.GetProperty("images");
        if (images.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid cover archive response.");
        // Return the short, stable CAA redirect, not an expiring URL or an untrusted
        // host supplied in a provider response. Discord fetches the image itself.
        return images.EnumerateArray().Any(i => i.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True)
            ? rootUrl + "/front-500" : null;
    }

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken token)
    {
        try { return await MusicMetadataHttp.JsonAsync(_http, new Uri(url), token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Credit(JsonElement group)
    {
        if (!group.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(credit.EnumerateArray().Select(c => (Text(c, "name") ??
            (c.TryGetProperty("artist", out var artist) ? Text(artist, "name") : null)) + Text(c, "joinphrase")));
    }

    private static string Quote(string value) => "\"" + Regex.Replace(value.Trim(), @"([+\-!(){}\[\]^""~*?:\\/|&])", @"\$1") + "\"";

    internal static bool IsCoverUrl(string? url) => url is not null && Regex.IsMatch(url,
        @"\Ahttps://coverartarchive\.org/(release|release-group)/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/front-500\z");

    private async Task<CacheEntry?> ReadCacheAsync(string path, string key, DateTimeOffset now, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8192) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            return entry is { Version: 1 } && entry.Key == key && entry.ExpiresAt > now &&
                entry.ExpiresAt <= now.AddDays(31) && (entry.Url is null || IsCoverUrl(entry.Url)) ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteCacheAsync(string path, CacheEntry entry, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry), token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning($"Could not cache album cover: {ex.Message}"); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
