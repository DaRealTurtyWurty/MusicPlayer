using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IListeningHistoryStore
{
    IReadOnlyDictionary<string, long> LoadPlayCounts();
    IReadOnlyList<ListeningHistoryEntry> LoadListeningHistory(int limit = 500);
    ListeningHistoryEntry RecordListen(Track track, DateTimeOffset playedAt);
}
