using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed partial class ArtistPhotoService : IArtistPhotoService, IArtistPhotoCustomization
{
    // Fanart.tv project keys identify distributed applications; this is the app's public project key.
    public const string DefaultFanartProjectKey = "4f1f297f0461a1f207119d428a0ad338";
    public static string ConfiguredFanartApiKey => Environment.GetEnvironmentVariable("MUSICPLAYER_FANART_API_KEY") ??
        Environment.GetEnvironmentVariable("MUSICPLAYER_FANART_API_KEY", EnvironmentVariableTarget.User) ?? DefaultFanartProjectKey;
    private const int ProviderRevision = 4;
    private sealed record DiskEntry(ArtistPhotoCredit? Attribution, byte[]? Image, DateTimeOffset ExpiresAt, int ProviderRevision = 0, string? ContextKey = null);
    private sealed record MemoryEntry(ArtistPhoto? Photo, DateTimeOffset ExpiresAt, long Access, string? ContextKey);
    private readonly string _directory;
    private readonly string _providerKey;
    private readonly ArtistPhotoProvider _provider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artistGates = new();
    private readonly SemaphoreSlim _workers = new(2, 2);
    private readonly object _memoryGate = new();
    private readonly Dictionary<string, MemoryEntry> _memory = [];
    private long _access;
    private int _writes;

    public ArtistPhotoService(string? cacheDirectory = null, HttpClient? client = null, string? fanartApiKey = null)
    {
        _directory = Path.GetFullPath(cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicPlayer", "artist-photos", "v1"));
        _provider = new ArtistPhotoProvider(client ?? MusicMetadataHttp.Client, fanartApiKey);
        _providerKey = string.IsNullOrWhiteSpace(fanartApiKey) ? "commons" : "fanart";
    }

    public Task<ArtistPhoto?> LoadAsync(string musicBrainzArtistId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = MusicBrainzId.Normalize(musicBrainzArtistId);
        return id is null ? Task.FromResult<ArtistPhoto?>(null) : Task.Run(() => LoadCoreAsync(id, cancellationToken), cancellationToken);
    }

    private async Task<ArtistPhoto?> LoadCoreAsync(string id, CancellationToken token, ArtistPhotoLookup? lookup = null)
    {
        var key = _providerKey + "-" + id;
        var gate = _artistGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_memoryGate)
            {
                if (_memory.TryGetValue(key, out var memory) && memory.ExpiresAt > DateTimeOffset.UtcNow &&
                    MatchesContext(memory.ContextKey, lookup?.Key, memory.Photo is not null))
                {
                    _memory[key] = memory with { Access = ++_access };
                    return memory.Photo;
                }
            }
            ArtistPhoto? stale = null;
            var disk = ReadDisk(key);
            if (disk is not null && MatchesContext(disk.ContextKey, lookup?.Key, disk.Image is not null))
            {
                try
                {
                    stale = disk.Image is not null && disk.Attribution is not null ? new ArtistPhoto(Decode(disk.Image), disk.Attribution) : null;
                    // Refresh older provider chains, keeping their photos available if offline.
                    if ((disk.ProviderRevision == ProviderRevision || disk.ProviderRevision == 3 && stale is not null) && disk.ExpiresAt > DateTimeOffset.UtcNow)
                    {
                        Remember(key, stale, disk.ExpiresAt, disk.ContextKey);
                        return stale;
                    }
                }
                catch (Exception ex) when (ArtistPhotoProvider.IsServiceFailure(ex)) { Trace.TraceWarning("Ignoring corrupt cached artist photo."); }
            }
            await _workers.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    var download = await _provider.FindAsync(id, timeout.Token, lookup).ConfigureAwait(false);
                    var photo = download?.Photo;
                    var expires = download?.HigherPriorityFailed == true ? DateTimeOffset.UtcNow.AddHours(1)
                        : DateTimeOffset.UtcNow.AddDays(photo is null ? 7 : 30);
                    token.ThrowIfCancellationRequested();
                    var contextKey = photo is null || download?.ContextDependent == true ? lookup?.Key : null;
                    Remember(key, photo, expires, contextKey);
                    WriteDisk(key, new(photo?.Attribution, download?.Bytes, expires, ProviderRevision, contextKey));
                    return photo;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ArtistPhotoProvider.IsServiceFailure(ex))
                {
                    // Keep an old photo available offline. Provider errors never become a seven-day miss.
                    Trace.TraceWarning($"Artist photo unavailable ({ex.GetType().Name}).");
                    Remember(key, stale, DateTimeOffset.UtcNow.AddMinutes(1), lookup?.Key);
                    return stale;
                }
            }
            finally { _workers.Release(); }
        }
        finally { gate.Release(); }
    }

    private static bool MatchesContext(string? cached, string? current, bool hasPhoto) =>
        hasPhoto && cached is null || cached == current;

    private void Remember(string key, ArtistPhoto? photo, DateTimeOffset expires, string? contextKey)
    {
        lock (_memoryGate)
        {
            _memory[key] = new(photo, expires, ++_access, contextKey);
            // A decoded photo is at most 512 x 512; cap retained images and misses together.
            while (_memory.Count > 64) _memory.Remove(_memory.MinBy(e => e.Value.Access).Key);
        }
    }

    private DiskEntry? ReadDisk(string key)
    {
        try
        {
            var file = new FileInfo(Path.Combine(_directory, key + ".json"));
            return file.Exists && file.Length <= 12 * 1024 * 1024
                ? JsonSerializer.Deserialize<DiskEntry>(File.ReadAllText(file.FullName)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void WriteDisk(string key, DiskEntry entry)
    {
        var path = Path.Combine(_directory, key + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            File.Move(temporary, path, overwrite: true);
            if (Interlocked.Increment(ref _writes) % 8 == 1) TrimDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Could not cache artist photo on disk."); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void TrimDisk()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("*.json").Where(f =>
        {
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            var split = stem.IndexOf('-');
            return split > 0 && stem[..split] is "commons" or "fanart" && MusicBrainzId.Normalize(stem[(split + 1)..]) is not null;
        }).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        for (var i = 0; i < files.Length; i++)
        {
            bytes += files[i].Length;
            if (bytes > 256L * 1024 * 1024 || i >= 1000) files[i].Delete();
        }
    }

    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) throw new InvalidDataException("Artist photo dimensions are invalid.");
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) image.DecodePixelWidth = Math.Min(512, width);
        else image.DecodePixelHeight = Math.Min(512, height);
        image.StreamSource = stream;
        image.EndInit();
        // Provider JPEGs can have invalid/asymmetric DPI (e.g. 96 x 1). WPF uses
        // physical size for layout, which otherwise stretches/crops portraits into gradients.
        var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
            image.Format, image.Palette, pixels, stride);
        normalized.Freeze();
        return normalized;
    }
}
