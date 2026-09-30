namespace MusicPlayer.Models;

public sealed record ListeningHistoryEntry(long Id, Track Track, DateTimeOffset PlayedAt)
{
    public string PlayedAtText => PlayedAt.ToLocalTime().ToString("g");
}
