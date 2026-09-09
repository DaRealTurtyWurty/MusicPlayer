using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

internal sealed partial class ArtistPhotoProvider(HttpClient client, string? fanartKey)
{
    internal sealed record Download(byte[] Bytes, ArtistPhoto Photo, bool HigherPriorityFailed = false, bool ContextDependent = false);

    internal async Task<Download?> FindAsync(string id, CancellationToken token, ArtistPhotoLookup? lookup = null)
    {
        Exception? providerFailure = null;
        try
        {
            var photo = await AudioDbAsync(id, token).ConfigureAwait(false);
            if (photo is not null) return photo;
        }
        catch (Exception ex) when (IsServiceFailure(ex) && !token.IsCancellationRequested) { providerFailure = ex; }
        if (!string.IsNullOrWhiteSpace(fanartKey))
        {
            try
            {
                var photo = await FanartAsync(id, token).ConfigureAwait(false);
                if (photo is not null) return photo with { HigherPriorityFailed = providerFailure is not null };
            }
            catch (Exception ex) when (IsServiceFailure(ex) && !token.IsCancellationRequested) { providerFailure = ex; }
        }
        // Reuse the authoritative relationships for Commons and Deezer.
        using var artist = await MusicMetadataHttp.JsonAsync(client,
            new Uri($"https://musicbrainz.org/ws/2/artist/{id}?inc=url-rels&fmt=json"), token).ConfigureAwait(false);
        try
        {
            var commons = await CommonsAsync(artist.RootElement, token).ConfigureAwait(false);
            if (commons is not null) return commons with { HigherPriorityFailed = providerFailure is not null };
        }
        catch (Exception ex) when (IsServiceFailure(ex) && !token.IsCancellationRequested) { providerFailure = ex; }
        var deezer = await DeezerAsync(artist.RootElement, token, lookup).ConfigureAwait(false);
        // An outage must not become a definitive miss or pin a lower-priority photo for 30 days.
        if (deezer is null && providerFailure is not null) throw new HttpRequestException("Artist photo provider unavailable.", providerFailure);
        return deezer is null ? null : deezer with { HigherPriorityFailed = providerFailure is not null };
    }

    private async Task<Download?> AudioDbAsync(string id, CancellationToken token)
    {
        // TheAudioDB's documented public free key; look up by MBID, never guess from a name.
        using var document = await MusicMetadataHttp.JsonAsync(client,
            new Uri($"https://www.theaudiodb.com/api/v1/json/123/artist-mb.php?i={id}"), token).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("TheAudioDB returned an error.");
        var artists = root.GetProperty("artists");
        if (artists.ValueKind == JsonValueKind.Null) return null;
        foreach (var artist in artists.EnumerateArray())
        {
            if (MusicBrainzId.Normalize(Text(artist, "strMusicBrainzID")) != id) continue;
            var artistId = Text(artist, "idArtist");
            if (artistId is null || !Regex.IsMatch(artistId, "^[1-9][0-9]*$")) continue;
            if (!Uri.TryCreate(Text(artist, "strArtistThumb"), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host is not ("www.theaudiodb.com" or "r2.theaudiodb.com") ||
                !uri.AbsolutePath.StartsWith("/images/media/artist/thumb/", StringComparison.Ordinal)) continue;
            var bytes = await MusicMetadataHttp.BytesAsync(client, uri, 8 * 1024 * 1024, token).ConfigureAwait(false);
            return new(bytes, new(ArtistPhotoService.Decode(bytes), new("TheAudioDB", $"https://www.theaudiodb.com/artist/{artistId}",
                "TheAudioDB", "Rights retained by copyright owner", null, Plain(Text(artist, "strArtist")))));
        }
        return null;
    }

    private async Task<Download?> FanartAsync(string id, CancellationToken token)
    {
        JsonDocument document;
        try { document = await MusicMetadataHttp.JsonAsync(client, new Uri($"https://webservice.fanart.tv/v3.2/music/{id}"), token, fanartKey).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("artistthumb", out var thumbs)) return null;
            foreach (var thumb in thumbs.EnumerateArray().OrderByDescending(t => int.TryParse(Text(t, "likes"), out var likes) ? likes : 0).Take(3))
            {
                var url = Text(thumb, "url");
                if (!ImageUrl(url, out var imageUri)) continue;
                var bytes = await MusicMetadataHttp.BytesAsync(client, imageUri!, 8 * 1024 * 1024, token).ConfigureAwait(false);
                return new(bytes, new(ArtistPhotoService.Decode(bytes), new("Fanart.tv", $"https://fanart.tv/artist/{id}/", "Fanart.tv", "Rights retained by copyright owner",
                    null, Text(document.RootElement, "name") ?? "Artist photo")));
            }
        }
        return null;
    }

    private async Task<Download?> DeezerAsync(JsonElement artist, CancellationToken token, ArtistPhotoLookup? lookup)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in artist.GetProperty("relations").EnumerateArray())
        {
            if (!relation.TryGetProperty("url", out var url) ||
                !Uri.TryCreate(Text(url, "resource"), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.Host is not ("www.deezer.com" or "deezer.com")) continue;
            var match = Regex.Match(uri.AbsolutePath, "^/(?:[a-z]{2}/)?artist/([1-9][0-9]*)/?$");
            if (match.Success) ids.Add(match.Groups[1].Value);
        }
        if (ids.Count > 1) return null; // Don't override conflicting authoritative links with a name search.
        var searched = ids.Count == 0;
        var id = searched ? lookup is null ? null : await SearchDeezerAsync(lookup, token).ConfigureAwait(false) : ids.Single();
        if (id is null) return null;
        JsonDocument document;
        try { document = await MusicMetadataHttp.JsonAsync(client, new Uri($"https://api.deezer.com/artist/{id}"), token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) && number == 800) return null;
                throw new InvalidDataException("Deezer returned an API error.");
            }
            if (!root.TryGetProperty("id", out var returnedId) || returnedId.ToString() != id || Text(root, "type") != "artist") return null;
            if (!Uri.TryCreate(Text(root, "picture_big"), UriKind.Absolute, out var imageUri) ||
                imageUri.Scheme != "https" || imageUri.Host is not ("cdn-images.dzcdn.net" or "e-cdns-images.dzcdn.net")) return null;
            var imageMatch = Regex.Match(imageUri.AbsolutePath, "^/images/artist/([a-f0-9]{32})/");
            if (!imageMatch.Success || imageMatch.Groups[1].Value is "d41d8cd98f00b204e9800998ecf8427e" or "00000000000000000000000000000000") return null;
            var bytes = await MusicMetadataHttp.BytesAsync(client, imageUri, 8 * 1024 * 1024, token).ConfigureAwait(false);
            return new(bytes, new(ArtistPhotoService.Decode(bytes), new("Deezer", $"https://www.deezer.com/artist/{id}",
                "Deezer", "Rights retained by copyright owner", null, Plain(Text(root, "name")))), ContextDependent: searched);
        }
    }

    private async Task<Download?> CommonsAsync(JsonElement artist, CancellationToken token)
    {
        string? entityId = null;
        foreach (var relation in artist.GetProperty("relations").EnumerateArray())
        {
            if (Text(relation, "type") != "wikidata" || !relation.TryGetProperty("url", out var url)) continue;
            if (Uri.TryCreate(Text(url, "resource"), UriKind.Absolute, out var uri) && uri.Host is "www.wikidata.org" or "wikidata.org")
            {
                var part = uri.AbsolutePath.Split('/').Last();
                if (Regex.IsMatch(part, "^Q[1-9][0-9]*$")) { entityId = part; break; }
            }
        }
        if (entityId is null) return null;
        using var wikidata = await MusicMetadataHttp.JsonAsync(client,
            new Uri($"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={entityId}&props=claims&format=json"), token).ConfigureAwait(false);
        ThrowApiError(wikidata.RootElement);
        var entity = wikidata.RootElement.GetProperty("entities").GetProperty(entityId);
        if (!entity.TryGetProperty("claims", out var claims) || !claims.TryGetProperty("P18", out var images)) return null;
        foreach (var image in images.EnumerateArray().Where(i => Text(i, "rank") != "deprecated")
                     .OrderByDescending(i => Text(i, "rank") == "preferred").Take(3))
        {
            var snak = image.GetProperty("mainsnak");
            if (!snak.TryGetProperty("datavalue", out var data)) continue;
            var file = Text(data, "value");
            if (string.IsNullOrWhiteSpace(file) || !(file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) continue;
            using var commons = await MusicMetadataHttp.JsonAsync(client, new Uri(
                "https://commons.wikimedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url%7Cextmetadata%7Cmime" +
                "&iiurlwidth=500&iiextmetadatalanguage=en&iiextmetadatafilter=Artist%7CAttribution%7CCredit%7CLicenseShortName%7CLicenseUrl%7CObjectName" +
                "&titles=" + Uri.EscapeDataString("File:" + file)), token).ConfigureAwait(false);
            ThrowApiError(commons.RootElement);
            foreach (var page in commons.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
            {
                if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0) continue;
                var item = info[0];
                if (Text(item, "mime") is not ("image/jpeg" or "image/png")) continue;
                if (!ImageUrl(Text(item, "thumburl"), out var imageUri)) continue;
                var metadata = item.GetProperty("extmetadata");
                string Meta(string key) => metadata.TryGetProperty(key, out var value) ? Plain(Text(value, "value")) : "";
                var license = Meta("LicenseShortName");
                var credit = Meta("Attribution");
                if (string.IsNullOrWhiteSpace(credit)) credit = Meta("Artist");
                var source = Text(item, "descriptionurl");
                if (string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(credit) || !WebUrl(source)) continue;
                var licenseUrl = Meta("LicenseUrl");
                if (licenseUrl.StartsWith("//", StringComparison.Ordinal)) licenseUrl = "https:" + licenseUrl;
                if (license.StartsWith("CC BY", StringComparison.OrdinalIgnoreCase) && !WebUrl(licenseUrl)) continue;
                var bytes = await MusicMetadataHttp.BytesAsync(client, imageUri!, 8 * 1024 * 1024, token).ConfigureAwait(false);
                var title = Meta("ObjectName");
                return new(bytes, new(ArtistPhotoService.Decode(bytes), new("Wikimedia Commons", source!, credit, license, WebUrl(licenseUrl) ? licenseUrl : null,
                    string.IsNullOrWhiteSpace(title) ? file : title)));
            }
        }
        return null;
    }

    internal static bool IsServiceFailure(Exception ex) => ex is HttpRequestException or OperationCanceledException or JsonException or
        InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException or NotSupportedException or ArgumentException;
    private static void ThrowApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("Wikimedia API returned an error.");
    }
    internal static bool WebUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    private static bool ImageUrl(string? url, out Uri? uri) => Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
        uri.Host is "upload.wikimedia.org" or "thumb.wikimedia.org" or "assets.fanart.tv";
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Plain(string? html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]*>", " ",
        RegexOptions.None, TimeSpan.FromSeconds(1))), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}
