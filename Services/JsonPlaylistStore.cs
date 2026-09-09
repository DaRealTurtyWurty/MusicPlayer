using System.IO;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class JsonPlaylistStore(string? path = null) : IPlaylistStore
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "playlists.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlyList<Playlist> Load()
    {
        if (!File.Exists(_path)) return [];
        var saved = JsonSerializer.Deserialize<List<SavedPlaylist>>(File.ReadAllText(_path), Options)
                    ?? throw new InvalidDataException("The playlist file is empty or invalid.");
        return saved.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Tracks is null)
                throw new InvalidDataException("A saved playlist is invalid.");
            var playlist = new Playlist { Id = item.Id, Name = item.Name };
            foreach (var track in item.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.FilePath) || track.DurationTicks < 0)
                    throw new InvalidDataException("A saved track is invalid.");
                playlist.Tracks.Add(new Track
                {
                    FilePath = track.FilePath, Title = track.Title ?? Path.GetFileNameWithoutExtension(track.FilePath),
                    Artist = track.Artist, Album = track.Album, Duration = TimeSpan.FromTicks(track.DurationTicks),
                    MusicBrainzArtistId = track.MusicBrainzArtistId, MetadataVersion = track.MetadataVersion
                });
            }

            return playlist;
        }).ToList();
    }

    public void Save(IEnumerable<Playlist> playlists)
    {
        var saved = playlists.Select(p => new SavedPlaylist(p.Id, p.Name,
            p.Tracks.Select(t => new SavedTrack(t.FilePath, t.Title, t.Artist, t.Album, t.Duration.Ticks,
                t.MusicBrainzArtistId, t.MetadataVersion)).ToList()));
        var folder = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(folder);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(saved, Options));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed record SavedPlaylist(Guid Id, string Name, List<SavedTrack> Tracks);

    private sealed record SavedTrack(string FilePath, string? Title, string? Artist, string? Album, long DurationTicks,
        string? MusicBrainzArtistId = null, int MetadataVersion = 0);
}
