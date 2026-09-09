using System.IO;
using System.Text;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed record LibraryRefreshResult(IReadOnlyList<Track> Updates, IReadOnlyList<Track> Added, int Failed);
public sealed record LocateSearchResult(IReadOnlyList<Track> Matches, int Failed);

public interface ILibraryRefreshService
{
    Task<LibraryRefreshResult> RefreshAsync(IReadOnlyList<Track> tracks, IReadOnlyList<WatchedMusicFolder> folders,
        bool force, CancellationToken cancellationToken);
    Task<LocateSearchResult> FindMatchesAsync(Track missing, string folder, CancellationToken cancellationToken);
    Task<Track> ReadFileAsync(string path, CancellationToken cancellationToken);
}

public sealed class LibraryRefreshService(IMetadataService metadata) : ILibraryRefreshService
{
    public Task<LibraryRefreshResult> RefreshAsync(IReadOnlyList<Track> tracks, IReadOnlyList<WatchedMusicFolder> folders,
        bool force, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var updates = new List<Track>();
        var added = new List<Track>();
        var known = tracks.Select(t => Path.GetFullPath(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(track.FilePath);
                // Reading Length distinguishes missing files from permission and I/O errors.
                var size = info.Length;
                var modified = info.LastWriteTimeUtc.Ticks;
                if (force || track.IsMissing || size != track.FileSize || modified != track.LastWriteTimeUtcTicks)
                    updates.Add(ReadStableFile(track.FilePath));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!track.IsMissing) updates.Add(track.WithFileState(track.FileSize, track.LastWriteTimeUtcTicks, true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }
        }
        foreach (var folder in folders)
        {
            if (!folder.DiscoverNewTracks) continue;
            foreach (var path in EnumerateAudioFiles(folder.Path, folder.IncludeSubdirectories, () => failed++, cancellationToken))
            {
                if (!known.Add(Path.GetFullPath(path))) continue;
                try
                {
                    var discovered = ReadStableFile(path);
                    discovered.ExplicitlyAddedToLibrary = true;
                    added.Add(discovered);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }
            }
        }
        return new LibraryRefreshResult(updates, added, failed);
    }, cancellationToken);

    public Task<LocateSearchResult> FindMatchesAsync(Track missing, string folder, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var matches = new List<Track>();
        var failed = 0;
        var title = NormalizeTag(missing.Title);
        var artist = NormalizeTag(missing.Artist);
        // An absent artist is insufficient evidence for an automatic match.
        if (title.Length == 0 || artist.Length == 0) return new LocateSearchResult(matches, failed);
        foreach (var path in EnumerateAudioFiles(folder, true, () => failed++, cancellationToken))
        {
            try
            {
                var track = ReadStableFile(path);
                if (MetadataMatches(missing, track)) matches.Add(track);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }
        }
        return new LocateSearchResult(matches.OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(), failed);
    }, cancellationToken);

    public Task<Track> ReadFileAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadStableFile(path);
    }, cancellationToken);

    private Track ReadStableFile(string path)
    {
        if (!LibraryScanner.IsSupportedFile(path)) throw new InvalidDataException("Choose a supported audio file.");
        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        var track = metadata.ReadTrack(Path.GetFullPath(path));
        info.Refresh();
        if (info.Length != size || info.LastWriteTimeUtc.Ticks != modified)
            throw new IOException("The file is still changing. Try again when copying or tag editing has finished.");
        return track.WithFileState(size, modified, false);
    }

    private static string NormalizeTag(string? value) => string.Join(" ",
        (value ?? "").Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static bool MetadataMatches(Track expected, Track candidate) =>
        NormalizeTag(expected.Title).Length > 0 && NormalizeTag(expected.Artist).Length > 0 &&
        NormalizeTag(expected.Title) == NormalizeTag(candidate.Title) && NormalizeTag(expected.Artist) == NormalizeTag(candidate.Artist);

    private static IEnumerable<string> EnumerateAudioFiles(string root, bool recursive, Action failed, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            string[] files;
            string[] folders;
            try
            {
                files = Directory.GetFiles(directory);
                folders = recursive ? Directory.GetDirectories(directory) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed(); continue; }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (LibraryScanner.IsSupportedFile(file)) yield return file;
            }
            foreach (var folder in folders)
            {
                try
                {
                    // Do not follow directory links or junction cycles outside the chosen folder.
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) pending.Push(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed(); }
            }
        }
    }
}
