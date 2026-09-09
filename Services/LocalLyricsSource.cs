using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>Loads an explicit lyrics file, or a same-basename TTML (preferred) or LRC sidecar.</summary>
public sealed class LocalLyricsSource : ILocalLyricsSource
{
    public Task<LocalLyricsResult> LoadAsync(string audioFilePath, TimeSpan? duration = null,
        string? lyricsFilePath = null, CancellationToken cancellationToken = default)
    {
        // Discovery and parsing also stay off the UI thread (music may be on a network drive).
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? path = lyricsFilePath;
            try
            {
                FileStream Open(string candidate) => new(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, useAsync: true);
                path = Path.GetFullPath(lyricsFilePath ?? Path.ChangeExtension(audioFilePath, ".ttml"));
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".lrc" or ".ttml"))
                    return new LocalLyricsResult(LocalLyricsStatus.Invalid, path, Error: "Choose an .lrc or .ttml file.");

                FileStream stream;
                try { stream = Open(path); }
                catch (FileNotFoundException) when (lyricsFilePath is null)
                {
                    path = Path.GetFullPath(Path.ChangeExtension(audioFilePath, ".lrc"));
                    extension = ".lrc";
                    stream = Open(path);
                }
                // Only absence falls back. An invalid/unreadable TTML must not silently pick a different version.
                await using var ownedStream = stream;
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var document = extension == ".ttml" ? new TtmlParser().Parse(text, duration, cancellationToken)
                    : new LrcParser().Parse(text, duration, cancellationToken);
                return new LocalLyricsResult(document.HasLyrics ? LocalLyricsStatus.Loaded : LocalLyricsStatus.Invalid,
                    path, document, document.HasLyrics ? null : document.Diagnostics.FirstOrDefault()?.Message ?? "No timed lyrics found in this file.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new LocalLyricsResult(LocalLyricsStatus.NotFound, path);
            }
            catch (DecoderFallbackException)
            {
                return new LocalLyricsResult(LocalLyricsStatus.Invalid, path, Error: "Lyrics must be UTF-8 or Unicode with a byte-order mark.");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return new LocalLyricsResult(LocalLyricsStatus.Invalid, path, Error: ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new LocalLyricsResult(LocalLyricsStatus.Unavailable, path, Error: ex.Message);
            }
        }, cancellationToken);
    }
}
