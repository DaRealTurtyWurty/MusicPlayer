using System.IO;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class JsonLibraryStore(string? path = null) : ILibraryStore
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "library.json");

    public IReadOnlyList<Track> Load()
    {
        if (!File.Exists(_path)) return [];
        var saved = JsonSerializer.Deserialize<List<SavedTrack>>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("The library file is empty or invalid.");
        return saved.Select(t =>
        {
            if (string.IsNullOrWhiteSpace(t.FilePath) || t.DurationTicks < 0)
                throw new InvalidDataException("A saved library track is invalid.");
            return new Track
            {
                FilePath = t.FilePath, Title = t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath),
                Artist = t.Artist, Album = t.Album, Duration = TimeSpan.FromTicks(t.DurationTicks)
            };
        }).ToArray();
    }

    public void Save(IEnumerable<Track> tracks)
    {
        var saved = tracks.Select(t => new SavedTrack(t.FilePath, t.Title, t.Artist, t.Album, t.Duration.Ticks));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(saved));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed record SavedTrack(string FilePath, string? Title, string? Artist, string? Album, long DurationTicks);
}
