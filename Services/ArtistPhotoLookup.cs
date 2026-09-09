using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

internal sealed record ArtistPhotoLookup(string Name, ArtistPhotoLookup.Song[] Songs, string Key)
{
    internal sealed record Song(string Album, string Title);
    internal static string Normalize(string value) => ArtistPhotoService.NormalizeArtistName(value)
        .Replace('\u2019', '\'').Replace('\u2018', '\'');

    internal static ArtistPhotoLookup? Create(string artistName, IReadOnlyList<Track> tracks)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return null;
        var name = Normalize(artistName);
        if (name is "UNKNOWN ARTIST" or "VARIOUS ARTISTS") return null;
        // Copy the evidence before doing asynchronous work; don't retain mutable library tracks.
        var songs = tracks.Where(t => !string.IsNullOrWhiteSpace(t.Album) && !string.IsNullOrWhiteSpace(t.Title) &&
                (string.IsNullOrWhiteSpace(t.Artist) || Normalize(t.Artist) == name))
            .Select(t => new Song(Normalize(t.Album!), Normalize(t.Title)))
            .Where(t => t.Album != "UNKNOWN ALBUM").Distinct()
            .OrderBy(t => t.Album, StringComparer.Ordinal).ThenBy(t => t.Title, StringComparer.Ordinal).ToArray();
        if (songs.Length == 0) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Name = name, Songs = songs }))));
        return new(name, songs, key);
    }
}
