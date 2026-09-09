using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed record WatchedMusicFolder(string Path, bool IncludeSubdirectories, bool DiscoverNewTracks = true);

public interface ILibraryMaintenanceStore
{
    IReadOnlyList<WatchedMusicFolder> LoadMusicFolders();
    void SaveMusicFolder(WatchedMusicFolder folder);
    void RelocateTrack(string originalPath, Track replacement);
}
