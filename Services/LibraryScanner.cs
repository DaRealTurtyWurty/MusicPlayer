using System.IO;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class LibraryScanner : ILibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".wav",
        ".m4a",
        ".ogg"
    };

    public static bool IsSupportedFile(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    private readonly IMetadataService _metadataService;

    public LibraryScanner(IMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    public Task<IReadOnlyList<Track>> ScanAsync(string folderPath)
    {
        return Task.Run<IReadOnlyList<Track>>(() =>
        {
            var tracks = new List<Track>();

            var files = Directory.EnumerateFiles(
                folderPath,
                "*.*",
                SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                var extension = Path.GetExtension(filePath);

                if (!SupportedExtensions.Contains(extension))
                    continue;

                try
                {
                    tracks.Add(_metadataService.ReadTrack(filePath));
                }
                catch
                {
                    // One bad/corrupt file shouldn't kill the whole scan.
                }
            }

            return tracks;
        });
    }
}