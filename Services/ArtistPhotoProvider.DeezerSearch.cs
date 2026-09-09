using System.IO;
using System.Text.Json;

namespace MusicPlayer.Services;

internal sealed partial class ArtistPhotoProvider
{
    private async Task<string?> SearchDeezerAsync(ArtistPhotoLookup lookup, CancellationToken token)
    {
        var artists = await DeezerListAsync("search/artist?q=" + Uri.EscapeDataString(lookup.Name), token).ConfigureAwait(false);
        if (artists is null) return null; // Never infer uniqueness from a truncated search.
        var exactArtists = artists.Where(a => ArtistPhotoLookup.Normalize(Text(a, "name") ?? "") == lookup.Name).ToArray();
        if (exactArtists.Any(a => DeezerId(a) is null)) throw new InvalidDataException("Deezer returned an invalid candidate ID.");
        var candidates = exactArtists.Select(a => DeezerId(a)!).Distinct().ToArray();
        if (candidates.Length is 0 or > 8) return null;
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var albums = await DeezerListAsync($"artist/{candidate}/albums?", token).ConfigureAwait(false);
            if (albums is null) return null;
            var matchingAlbums = albums.Where(a => lookup.Songs.Any(s => s.Album == ArtistPhotoLookup.Normalize(Text(a, "title") ?? "")))
                .Select(a => new { Id = DeezerId(a), Title = ArtistPhotoLookup.Normalize(Text(a, "title") ?? "") })
                .DistinctBy(a => a.Id).ToArray();
            if (matchingAlbums.Any(a => a.Id is null)) throw new InvalidDataException("Deezer returned an invalid album ID.");
            if (matchingAlbums.Length > 12) return null;
            foreach (var album in matchingAlbums)
            {
                using var document = await MusicMetadataHttp.JsonAsync(client,
                    new Uri($"https://api.deezer.com/album/{album.Id}"), token).ConfigureAwait(false);
                var root = document.RootElement;
                CheckDeezerSearchError(root);
                if (DeezerId(root) != album.Id || ArtistPhotoLookup.Normalize(Text(root, "title") ?? "") != album.Title ||
                    !root.TryGetProperty("artist", out var albumArtist) || DeezerId(albumArtist) != candidate) continue;
                if (!root.TryGetProperty("tracks", out var trackList) || !trackList.TryGetProperty("data", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Deezer returned no album tracks.");
                if (tracks.EnumerateArray().Any(track => track.TryGetProperty("artist", out var credit) && DeezerId(credit) == candidate &&
                    lookup.Songs.Any(s => s.Album == album.Title && s.Title == ArtistPhotoLookup.Normalize(Text(track, "title") ?? ""))))
                {
                    supported.Add(candidate);
                    break;
                }
                // Missing tracks could contain a competing match; don't treat partial evidence as a negative.
                if (!string.IsNullOrEmpty(Text(trackList, "next")) ||
                    !root.TryGetProperty("nb_tracks", out var count) || !count.TryGetInt32(out var total) || total != tracks.GetArrayLength()) return null;
            }
            if (supported.Count > 1) return null; // Conflicting or same-name catalogue evidence needs a manual choice.
        }
        return supported.Count == 1 ? supported.Single() : null;
    }

    private static string? DeezerId(JsonElement item) => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
        id.TryGetInt64(out var number) && number > 0 ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    private static void CheckDeezerSearchError(JsonElement root)
    {
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("Deezer catalogue lookup failed.");
    }

    private async Task<List<JsonElement>?> DeezerListAsync(string pathAndQuery, CancellationToken token)
    {
        var items = new List<JsonElement>();
        // Reconstruct same-host pages instead of following arbitrary response URLs.
        for (var page = 0; page < 3; page++)
        {
            using var document = await MusicMetadataHttp.JsonAsync(client,
                new Uri($"https://api.deezer.com/{pathAndQuery}&limit=100&index={page * 100}"), token).ConfigureAwait(false);
            var root = document.RootElement;
            CheckDeezerSearchError(root);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("total", out var totalValue) || !totalValue.TryGetInt32(out var total) || total < 0)
                throw new InvalidDataException("Deezer returned an incomplete catalogue response.");
            if (total > 300) return null;
            items.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            if (items.Count >= total && string.IsNullOrEmpty(Text(root, "next"))) return items;
            if (data.GetArrayLength() != 100) return null;
        }
        return null;
    }
}
