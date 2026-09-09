using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ILibraryScanner
{
    Task<IReadOnlyList<Track>> ScanAsync(string folderPath);
}