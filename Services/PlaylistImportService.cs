using System.IO;
using System.Text;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class PlaylistImportService(IMetadataService metadata) : IPlaylistImportService
{
    public Task<PlaylistImportResult> ImportTracksAsync(IReadOnlyList<string> paths,
        IProgress<PlaylistImportProgress>? progress = null) => Task.Run(() =>
    {
        var tracks = new List<Track>();
        var skipped = 0;
        var reporter = new ImportReporter(progress);
        reporter.Report(0, paths.Count, 0, null, force: true);
        for (var i = 0; i < paths.Count; i++)
        {
            if (TryRead(paths[i], out var track)) tracks.Add(track!);
            else skipped++;
            reporter.Report(i + 1, paths.Count, skipped, Path.GetFileName(paths[i]), force: i == paths.Count - 1);
        }
        return new PlaylistImportResult("Selected tracks", tracks, skipped);
    });

    public Task<PlaylistImportResult>
        ImportFileAsync(string path, IProgress<PlaylistImportProgress>? progress = null) => Task.Run(() =>
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var tracks = new List<Track>();
        var skipped = 0;
        var reporter = new ImportReporter(progress);
        var entries = new List<string>();
        reporter.Report(0, null, 0, null, force: true);
        // M3U8 is UTF-8; StreamReader also accepts a UTF-8 BOM.
        foreach (var rawLine in File.ReadLines(fullPath, new UTF8Encoding(false, true)))
        {
            var entry = rawLine.Trim();
            if (entry.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "This is an HLS streaming playlist. Choose an M3U8 playlist containing local music files.");
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            entries.Add(entry);
            reporter.Report(entries.Count, null, 0, null);
        }

        reporter.Report(0, entries.Count, 0, null, force: true);
        var processed = 0;
        foreach (var entry in entries)
        {
            reporter.Report(processed, entries.Count, skipped, Path.GetFileName(entry), force: processed == 0);
            try
            {
                string trackPath;
                if (Path.IsPathFullyQualified(entry)) trackPath = entry;
                else if (Uri.TryCreate(entry, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile)
                    {
                        skipped++;
                        continue;
                    }

                    trackPath = uri.LocalPath;
                }
                else trackPath = Path.GetFullPath(entry, directory);

                if (!TryRead(trackPath, out var track))
                {
                    skipped++;
                    continue;
                }

                tracks.Add(track!); // Keep the playlist's order and intentional duplicates.
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                skipped++;
            }
            finally
            {
                processed++;
                reporter.Report(processed, entries.Count, skipped, Path.GetFileName(entry),
                    force: processed == entries.Count);
            }
        }

        return new PlaylistImportResult(Path.GetFileNameWithoutExtension(fullPath), tracks, skipped);
    });

    public Task<PlaylistImportResult>
        ImportFolderAsync(string path, IProgress<PlaylistImportProgress>? progress = null) => Task.Run(() =>
    {
        var directory = new DirectoryInfo(path);
        var tracks = new List<Track>();
        var skipped = 0;
        var reporter = new ImportReporter(progress);
        reporter.Report(0, null, 0, null, force: true);
        var files = new List<FileInfo>();
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (LibraryScanner.IsSupportedFile(file.FullName)) files.Add(file);
            reporter.Report(files.Count, null, 0, null);
        }

        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName));
        reporter.Report(0, files.Count, 0, null, force: true);
        var processed = 0;
        foreach (var file in files)
        {
            reporter.Report(processed, files.Count, skipped, file.Name, force: processed == 0);
            if (TryRead(file.FullName, out var track)) tracks.Add(track!);
            else skipped++;
            reporter.Report(++processed, files.Count, skipped, file.Name, force: processed == files.Count);
        }

        return new PlaylistImportResult(
            string.IsNullOrWhiteSpace(directory.Name) ? "Imported playlist" : directory.Name, tracks, skipped);
    });

    private sealed class ImportReporter(IProgress<PlaylistImportProgress>? progress)
    {
        private long _lastReport;

        public void Report(int processed, int? total, int skipped, string? file, bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - _lastReport < 100) return;
            _lastReport = now;
            progress?.Report(new(processed, total, skipped, file));
        }
    }

    private bool TryRead(string path, out Track? track)
    {
        track = null;
        if (!LibraryScanner.IsSupportedFile(path) || !File.Exists(path)) return false;
        try
        {
            track = metadata.ReadTrack(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
