using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public static class PlaylistArtworkService
{
    private static readonly PlaylistArtworkCache Cache = new();

    public static Task<IReadOnlyList<ImageSource>> LoadCachedAsync(IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default) => Cache.LoadAsync(tracks, cancellationToken);

    public static IReadOnlyList<ImageSource> Load(IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        var covers = new List<ImageSource>(4);
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var encodedImages = new HashSet<string>();
        var decodedImages = new HashSet<string>();
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var albumKey = string.IsNullOrWhiteSpace(track.Album) || string.IsNullOrWhiteSpace(track.Artist)
                ? null
                : $"{track.Artist.Trim()}\0{track.Album.Trim()}";
            if (albumKey is not null && albums.Contains(albumKey)) continue;
            try
            {
                var data = track.ArtworkData;
                if (data is null && File.Exists(track.FilePath))
                {
                    using var file = TagLib.File.Create(track.FilePath);
                    data = file.Tag.Pictures.FirstOrDefault()?.Data.Data;
                }

                if (data is not { Length: > 0 }) continue;
                if (!encodedImages.Add(Convert.ToHexString(SHA256.HashData(data)))) continue;
                using var stream = new MemoryStream(data);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 256;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                // Compare decoded pixels too: identical artwork can have different file metadata/encoding.
                var thumbnail = new TransformedBitmap(image,
                    new ScaleTransform(32d / image.PixelWidth, 32d / image.PixelHeight));
                var normalized = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[normalized.PixelWidth * normalized.PixelHeight * 4];
                normalized.CopyPixels(pixels, normalized.PixelWidth * 4, 0);
                if (!decodedImages.Add(Convert.ToHexString(SHA256.HashData(pixels)))) continue;
                covers.Add(image);
                if (albumKey is not null) albums.Add(albumKey);
                if (covers.Count == 4) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing file or broken image should not prevent trying the next song.
            }
        }

        return covers;
    }
}

/// <summary>
/// Content-addressed playlist thumbnails. Frozen images are retained in a bounded process cache and
/// file-backed playlists are also stored in LocalAppData so reopening the app does not reread audio tags.
/// </summary>
public sealed class PlaylistArtworkCache
{
    private const string CacheVersion = "playlist-artwork-v1";
    private const long DefaultMemoryLimit = 64L * 1024 * 1024;
    private const long DefaultDiskLimit = 256L * 1024 * 1024;
    private const int DefaultEntryLimit = 256;

    private readonly string _cacheDirectory;
    private readonly long _memoryLimit;
    private readonly long _diskLimit;
    private readonly int _entryLimit;
    private readonly SemaphoreSlim _workers = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ImageSource>>>> _inflight = new();
    private readonly object _memoryGate = new();
    private readonly Dictionary<string, LinkedListNode<MemoryEntry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<MemoryEntry> _lru = new();
    private long _memoryBytes;
    private int _diskWrites;

    public PlaylistArtworkCache(string? cacheDirectory = null, long memoryLimitBytes = DefaultMemoryLimit,
        long diskLimitBytes = DefaultDiskLimit, int entryLimit = DefaultEntryLimit)
    {
        if (memoryLimitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        if (diskLimitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(diskLimitBytes));
        if (entryLimit <= 0) throw new ArgumentOutOfRangeException(nameof(entryLimit));
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "artwork-cache", "v1");
        _memoryLimit = memoryLimitBytes;
        _diskLimit = diskLimitBytes;
        _entryLimit = entryLimit;
    }

    public async Task<IReadOnlyList<ImageSource>> LoadAsync(IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0) return [];
        var snapshot = tracks.ToArray();
        var identity = await Task.Run(() => CreateIdentity(snapshot), cancellationToken).ConfigureAwait(false);
        if (TryGetMemory(identity.Key, out var cached)) return cached;

        var pending = _inflight.GetOrAdd(identity.Key, _ =>
            new Lazy<Task<IReadOnlyList<ImageSource>>>(() => LoadCoreAsync(identity, snapshot),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ImageSource>> LoadCoreAsync(CacheIdentity identity, IReadOnlyList<Track> tracks)
    {
        try
        {
            await _workers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryGetMemory(identity.Key, out var cached)) return cached;
                if (identity.Persistent && TryLoadDisk(identity.Key) is { } stored)
                {
                    AddMemory(identity.Key, stored);
                    return stored;
                }

                var generated = PlaylistArtworkService.Load(tracks);
                AddMemory(identity.Key, generated);
                if (identity.Persistent) TryWriteDisk(identity.Key, generated);
                return generated;
            }
            finally
            {
                _workers.Release();
            }
        }
        finally
        {
            _inflight.TryRemove(identity.Key, out _);
        }
    }

    private bool TryGetMemory(string key, out IReadOnlyList<ImageSource> images)
    {
        lock (_memoryGate)
        {
            if (!_memory.TryGetValue(key, out var node))
            {
                images = [];
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            images = node.Value.Images;
            return true;
        }
    }

    private void AddMemory(string key, IReadOnlyList<ImageSource> images)
    {
        var size = Math.Max(1, images.OfType<BitmapSource>().Sum(image =>
            (long)image.PixelWidth * image.PixelHeight * Math.Max(1, image.Format.BitsPerPixel / 8)));
        lock (_memoryGate)
        {
            if (_memory.Remove(key, out var existing))
            {
                _lru.Remove(existing);
                _memoryBytes -= existing.Value.Size;
            }
            var node = _lru.AddFirst(new MemoryEntry(key, images, size));
            _memory[key] = node;
            _memoryBytes += size;
            while ((_memoryBytes > _memoryLimit || _memory.Count > _entryLimit) && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _memory.Remove(oldest.Value.Key);
                _memoryBytes -= oldest.Value.Size;
            }
        }
    }

    private IReadOnlyList<ImageSource>? TryLoadDisk(string key)
    {
        var directory = Path.Combine(_cacheDirectory, key);
        try
        {
            var manifest = Path.Combine(directory, "count");
            if (!File.Exists(manifest)) return null;
            if (!int.TryParse(File.ReadAllText(manifest), out var count) || count is < 0 or > 4)
                throw new InvalidDataException("Invalid playlist artwork cache manifest.");
            var images = new ImageSource[count];
            for (var index = 0; index < count; index++)
            {
                using var stream = File.OpenRead(Path.Combine(directory, $"{index}.png"));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                images[index] = image;
            }
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
            return images;
        }
        catch (Exception)
        {
            TryDeleteDirectory(directory);
            return null;
        }
    }

    private void TryWriteDisk(string key, IReadOnlyList<ImageSource> images)
    {
        var target = Path.Combine(_cacheDirectory, key);
        if (Directory.Exists(target)) return;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            Directory.CreateDirectory(temporary);
            for (var index = 0; index < images.Count; index++)
            {
                if (images[index] is not BitmapSource bitmap) throw new InvalidDataException("Unsupported cached artwork.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(temporary, $"{index}.png"));
                encoder.Save(stream);
            }
            File.WriteAllText(Path.Combine(temporary, "count"), images.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try { Directory.Move(temporary, target); }
            catch (IOException) when (Directory.Exists(target)) { TryDeleteDirectory(temporary); }
            if (Interlocked.Increment(ref _diskWrites) % 32 == 1) TrimDiskCache();
        }
        catch (Exception)
        {
            TryDeleteDirectory(temporary);
        }
    }

    private void TrimDiskCache()
    {
        try
        {
            var entries = new DirectoryInfo(_cacheDirectory).EnumerateDirectories()
                .Where(directory => !directory.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(directory => new DiskEntry(directory,
                    directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)))
                .OrderBy(entry => entry.Directory.LastWriteTimeUtc).ToArray();
            var total = entries.Sum(entry => entry.Size);
            foreach (var entry in entries)
            {
                if (total <= _diskLimit) break;
                TryDeleteDirectory(entry.Directory.FullName);
                total -= entry.Size;
            }
            foreach (var temporary in new DirectoryInfo(_cacheDirectory).EnumerateDirectories("*.tmp"))
                if (temporary.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDeleteDirectory(temporary.FullName);
        }
        catch (Exception)
        {
        }
    }

    private static CacheIdentity CreateIdentity(IReadOnlyList<Track> tracks)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, CacheVersion);
        Append(hash, tracks.Count);
        var persistent = true;
        foreach (var track in tracks)
        {
            Append(hash, NormalizePath(track.FilePath));
            Append(hash, NormalizeTag(track.Artist));
            Append(hash, NormalizeTag(track.Album));
            Append(hash, track.IsMissing ? 1 : 0);
            var size = track.FileSize;
            var modified = track.LastWriteTimeUtcTicks;
            if (size is null || modified is null)
            {
                try
                {
                    var info = new FileInfo(track.FilePath);
                    if (info.Exists)
                    {
                        size = info.Length;
                        modified = info.LastWriteTimeUtc.Ticks;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                }
            }
            Append(hash, size ?? -1);
            Append(hash, modified ?? -1);
            if (size is null || modified is null)
            {
                persistent = false;
                if (track.ArtworkData is { Length: > 0 } data)
                {
                    Append(hash, 1);
                    hash.AppendData(SHA256.HashData(data));
                }
                else Append(hash, 0);
            }
        }
        return new CacheIdentity(Convert.ToHexString(hash.GetHashAndReset()), persistent);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }

    private static string NormalizeTag(string? value) => value?.Trim().ToUpperInvariant() ?? "";

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record MemoryEntry(string Key, IReadOnlyList<ImageSource> Images, long Size);
    private sealed record DiskEntry(DirectoryInfo Directory, long Size);
    private sealed record CacheIdentity(string Key, bool Persistent);
}
