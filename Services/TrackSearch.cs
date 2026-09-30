using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public static class TrackSearch
{
    // All terms must match. Quotes group phrases; prefixes restrict a term to a field.
    public static bool Matches(Track track, string query) => Regex.Matches(query,
        "(?:[^\\s\"]+:)?\"[^\"]*\"|[^\\s]+").Cast<Match>().All(match =>
    {
        var term = match.Value;
        var colon = term.IndexOf(':');
        var field = colon > 0 ? term[..colon].ToLowerInvariant() : "";
        var value = (colon > 0 ? term[(colon + 1)..] : term).Trim('"');
        if (field is not ("title" or "artist" or "album" or "albumartist" or "genre" or "year" or "path" or "missing"))
        {
            field = "";
            value = term.Trim('"');
        }
        string? text = field switch
        {
            "title" => track.Title, "artist" => track.Artist, "album" => track.Album,
            "albumartist" => track.AlbumArtist, "genre" => track.Genre,
            "year" => track.Year.ToString(), "path" => track.FilePath,
            "missing" => track.IsMissing.ToString(),
            "" => string.Join(" ", track.Title, track.Artist, track.Album, track.AlbumArtist,
                track.Genre, track.Year == 0 ? "" : track.Year.ToString()),
            _ => null
        };
        return text?.Contains(value, StringComparison.CurrentCultureIgnoreCase) == true;
    });
}
