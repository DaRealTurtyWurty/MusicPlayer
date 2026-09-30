using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed record WatchedMusicFolder(string Path, bool IncludeSubdirectories, bool DiscoverNewTracks = true);

public interface ILibraryMaintenanceStore
{
    IReadOnlyList<WatchedMusicFolder> LoadMusicFolders();
    void SaveMusicFolder(WatchedMusicFolder folder);
    void RemoveMusicFolder(string path) => throw new NotSupportedException("Folder removal is unavailable.");
    void RelocateTrack(string originalPath, Track replacement);
}
