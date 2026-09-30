using System.Globalization;
using System.IO;
using System.Text;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public static class PlaylistExportService
{
    public static void Export(Playlist playlist, string destination, bool relativePaths)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var lines = new List<string> { "#EXTM3U" };
        foreach (var track in playlist.Tracks)
        {
            var path = Path.GetFullPath(track.FilePath);
            if (path.IndexOfAny(['\r', '\n']) >= 0) throw new IOException("A track path contains a line break.");
            var label = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";
            lines.Add($"#EXTINF:{Math.Max(0, (long)track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)},{label.Replace('\r', ' ').Replace('\n', ' ')}");
            var entry = relativePaths ? Path.GetRelativePath(directory, path) : path;
            lines.Add(entry.StartsWith('#') ? "." + Path.DirectorySeparatorChar + entry : entry);
        }
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
