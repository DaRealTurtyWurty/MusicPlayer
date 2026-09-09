using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ILibraryStore
{
    IReadOnlyList<Track> Load();
    // Saves track metadata and explicit membership without deleting playback/history records.
    void Save(IEnumerable<Track> tracks);
}
