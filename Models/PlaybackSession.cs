namespace MusicPlayer.Models;

public sealed record PlaybackSession(Track? CurrentTrack, TimeSpan Position, IReadOnlyList<Track> Queue)
{
    public IReadOnlyList<PlaybackHistoryEntry> History { get; init; } = [];
}

public sealed record PlaybackHistoryEntry(Track Track, bool Recycled);
