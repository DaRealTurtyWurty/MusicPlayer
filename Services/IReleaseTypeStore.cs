using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IReleaseTypeStore
{
    IReadOnlyDictionary<string, ReleaseType> LoadReleaseTypes();
    void SaveReleaseType(string releaseKey, ReleaseType? type);
}
