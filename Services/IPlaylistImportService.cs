using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed record PlaylistImportResult(string Name, IReadOnlyList<Track> Tracks, int SkippedCount);

public sealed record PlaylistImportProgress(int Processed, int? Total, int Skipped, string? CurrentFile);

public interface IPlaylistImportService
{
    Task<PlaylistImportResult> ImportTracksAsync(IReadOnlyList<string> paths, IProgress<PlaylistImportProgress>? progress = null);
    Task<PlaylistImportResult> ImportFileAsync(string path, IProgress<PlaylistImportProgress>? progress = null);
    Task<PlaylistImportResult> ImportFolderAsync(string path, IProgress<PlaylistImportProgress>? progress = null);
}
