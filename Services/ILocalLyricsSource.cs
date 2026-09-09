using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface ILocalLyricsSource
{
    Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null,
        string? lyricsFilePath = null, CancellationToken cancellationToken = default);
}

public enum LocalLyricsStatus { Loaded, NotFound, Invalid, Unavailable }

public sealed record LocalLyricsResult(LocalLyricsStatus Status, string? FilePath,
    LyricsDocument? Document = null, string? Error = null)
{
    public bool IsEmbedded { get; init; }
}
