using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed partial class ArtistPhotoService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _customGates = new();
    public event EventHandler<ArtistPhotoChangedEventArgs>? PhotoChanged;

    internal static string NormalizeArtistName(string name) =>
        Regex.Replace(name.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").ToUpperInvariant();

    private static string CustomKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeArtistName(name))));
    }

    private string CustomPath(string key) => Path.Combine(_directory, "custom", key + ".png");

    public Task<ArtistPhoto?> LoadAsync(string? musicBrainzArtistId, string artistName, CancellationToken cancellationToken = default) =>
        LoadAsync(musicBrainzArtistId, artistName, [], cancellationToken);

    public async Task<ArtistPhoto?> LoadAsync(string? musicBrainzArtistId, string artistName, IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lookup = ArtistPhotoLookup.Create(artistName, tracks);
        var custom = await ReadCustomAsync(artistName, cancellationToken).ConfigureAwait(false);
        if (custom is not null) return custom;
        var automatic = MusicBrainzId.Normalize(musicBrainzArtistId) is { } id
            ? await Task.Run(() => LoadCoreAsync(id, cancellationToken, lookup), cancellationToken).ConfigureAwait(false) : null;
        // A photo chosen during the HTTP request must always win over its late result.
        return await ReadCustomAsync(artistName, cancellationToken).ConfigureAwait(false) ?? automatic;
    }

    private Task<ArtistPhoto?> ReadCustomAsync(string artistName, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(artistName)) return null;
        var key = CustomKey(artistName);
        var gate = _customGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = CustomPath(key);
            if (!File.Exists(path)) return null;
            var bytes = await ReadImageBytesAsync(path, token).ConfigureAwait(false);
            return new ArtistPhoto(Decode(bytes), new("Custom photo", "", "Selected by you", "User-selected image", null, artistName), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ArtistPhotoProvider.IsServiceFailure(ex) || ex is UnauthorizedAccessException))
        {
            Trace.TraceWarning("Could not read custom artist photo; using automatic artwork.");
            return null;
        }
        finally { gate.Release(); }
    }, token);

    private static async Task<byte[]> ReadImageBytesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, useAsync: true);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 8 MB.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    public async Task SetCustomPhotoAsync(string artistName, string filePath, CancellationToken cancellationToken = default)
    {
        var key = CustomKey(artistName);
        await Task.Run(async () =>
        {
            var gate = _customGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporary = null;
            try
            {
                var bytes = await ReadImageBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
                var image = Decode(bytes); // Validate before replacing an existing choice.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                var path = CustomPath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = File.Create(temporary)) encoder.Save(output);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                gate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
        PhotoChanged?.Invoke(this, new(artistName));
    }

    public async Task ResetCustomPhotoAsync(string artistName, CancellationToken cancellationToken = default)
    {
        var key = CustomKey(artistName);
        await Task.Run(async () =>
        {
            var gate = _customGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { cancellationToken.ThrowIfCancellationRequested(); File.Delete(CustomPath(key)); }
            catch (DirectoryNotFoundException) { } // Resetting an artist that never had an override is harmless.
            finally { gate.Release(); }
        }, cancellationToken).ConfigureAwait(false);
        PhotoChanged?.Invoke(this, new(artistName));
    }
}
