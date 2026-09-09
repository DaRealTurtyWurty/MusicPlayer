using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>Loads an explicit file, or a same-basename TTML, Lyricsfile or LRC sidecar, in that order.</summary>
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
                var candidates = lyricsFilePath is not null ? new[] { lyricsFilePath } : new[]
                {
                    Path.ChangeExtension(audioFilePath, ".ttml"), Path.ChangeExtension(audioFilePath, ".lyricsfile.yaml"),
                    Path.ChangeExtension(audioFilePath, ".lrc")
                };
                FileStream? stream = null;
                foreach (var candidate in candidates)
                {
                    path = Path.GetFullPath(candidate);
                    var candidateExtension = Path.GetExtension(path).ToLowerInvariant();
                    if (candidateExtension is not (".lrc" or ".ttml") && !path.EndsWith(".lyricsfile.yaml", StringComparison.OrdinalIgnoreCase))
                        return new LocalLyricsResult(LocalLyricsStatus.Invalid, path, Error: "Choose an .lrc, .ttml or .lyricsfile.yaml file.");
                    try { stream = Open(path); break; }
                    catch (FileNotFoundException) when (lyricsFilePath is null) { }
                }
                if (stream is null) return new LocalLyricsResult(LocalLyricsStatus.NotFound, path);
                // Only absence falls back. Invalid/unreadable lyrics must not silently select another version.
                await using var ownedStream = stream;
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var document = Path.GetExtension(path!).ToLowerInvariant() switch
                {
                    ".ttml" => new TtmlParser().Parse(text, duration, cancellationToken),
                    ".yaml" => new LyricsfileParser().Parse(text, duration, cancellationToken),
                    _ => new LrcParser().Parse(text, duration, cancellationToken)
                };
                return new LocalLyricsResult(document.HasLyrics || document.IsInstrumental ? LocalLyricsStatus.Loaded : LocalLyricsStatus.Invalid,
                    path, document, document.HasLyrics || document.IsInstrumental ? null : document.Diagnostics.FirstOrDefault()?.Message ?? "No lyrics found in this file.");
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
