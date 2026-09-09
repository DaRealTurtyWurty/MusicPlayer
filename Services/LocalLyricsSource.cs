using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>Loads only an explicitly selected LRC or an exact same-basename sidecar.</summary>
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
                path = Path.GetFullPath(lyricsFilePath ?? Path.ChangeExtension(audioFilePath, ".lrc"));
                if (!Path.GetExtension(path).Equals(".lrc", StringComparison.OrdinalIgnoreCase))
                    return new LocalLyricsResult(LocalLyricsStatus.Invalid, path, Error: "Choose an .lrc file.");

                // Windows resolves casing, so Song.LRC also matches Song.flac. Opening directly
                // distinguishes an absent sidecar from access errors that File.Exists would hide.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var document = new LrcParser().Parse(text, duration, cancellationToken);
                return new LocalLyricsResult(document.HasLyrics ? LocalLyricsStatus.Loaded : LocalLyricsStatus.Invalid,
                    path, document, document.HasLyrics ? null : "No timed lyrics found in this file.");
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
