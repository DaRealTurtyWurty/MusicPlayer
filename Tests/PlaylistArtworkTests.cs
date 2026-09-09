using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicPlayer.Models;
using MusicPlayer.Services;

internal static class PlaylistArtworkTests
{
    public static async Task RunAsync(string fixtureFolder)
    {
        var red = Artwork(Colors.IndianRed);
        var green = Artwork(Colors.SeaGreen);
        var blue = Artwork(Colors.SteelBlue);
        var gold = Artwork(Colors.Goldenrod);
        var tracks = new[]
        {
            Track("first", red, "Album A"), Track("same album", green, "Album A"),
            Track("same file bytes", red), Track("same pixels, other encoding", Artwork(Colors.IndianRed, bmp: true)),
            Track("missing", null), Track("invalid", [1, 2, 3]),
            Track("green", green), Track("blue", blue), Track("gold", gold)
        };
        var covers = PlaylistArtworkService.Load(tracks);
        Check(covers.Count == 4 && covers.All(c => c.IsFrozen), "Artwork selection searches beyond the first four songs and freezes four unique covers");
        Check(PlaylistArtworkService.Load(tracks.Take(4).ToArray()).Count == 1, "Same album, identical bytes and differently encoded identical images are not repeated");
        Check(PlaylistArtworkService.Load([Track("one", red), Track("two", green)]).Count == 2,
            "Two unique covers remain two covers instead of repeating to fill four slots");
        Check(PlaylistArtworkService.Load([Track("none", null), Track("bad", [0])]).Count == 0,
            "Missing and corrupt artwork retains the empty placeholder");
        var playlist = new Playlist();
        foreach (var track in tracks) playlist.Tracks.Add(track);
        Check(playlist.CoverTracks.Count == tracks.Length, "Playlist supplies later songs for unique cover selection");
        var coverSnapshot = playlist.CoverTracks;
        Check(ReferenceEquals(coverSnapshot, playlist.CoverTracks), "Unchanged playlists retain a stable cover-track snapshot");
        playlist.Tracks.Add(Track("new", gold));
        Check(!ReferenceEquals(coverSnapshot, playlist.CoverTracks), "Playlist edits replace the cover-track snapshot");

        foreach (var (name, data) in new[] { ("01.wav", red), ("02.wav", red), ("03.wav", green) })
        {
            using var file = TagLib.File.Create(Path.Combine(fixtureFolder, name));
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(data)) { MimeType = "image/png", Type = TagLib.PictureType.FrontCover }];
            file.Save();
        }
        var savedTracks = new[] { "01.wav", "02.wav", "03.wav" }.Select(name => new Track { FilePath = Path.Combine(fixtureFolder, name), Title = name }).ToArray();
        Check(PlaylistArtworkService.Load(savedTracks).Count == 2, "Saved playlists also deduplicate artwork read from music files");

        using var cacheDirectory = new TemporaryTestDirectory("MusicPlayerArtworkCacheTests");
        var cachedTracks = new[] { "01.wav", "02.wav", "03.wav" }
            .Select(name => new TagLibMetadataService().ReadTrack(Path.Combine(fixtureFolder, name))).ToArray();
        var cache = new PlaylistArtworkCache(cacheDirectory.Path);
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.LoadAsync(cachedTracks)));
        Check(requests.All(result => ReferenceEquals(result, requests[0])) && requests[0].Count == 2,
            "Concurrent and repeated playlist covers share one in-memory result");

        var cacheEntry = Directory.GetDirectories(cacheDirectory.Path).Single();
        var cachedImage = Directory.GetFiles(cacheEntry, "*.png").First();
        File.WriteAllBytes(cachedImage, [0, 1, 2]);
        var recovered = await new PlaylistArtworkCache(cacheDirectory.Path).LoadAsync(cachedTracks);
        Check(recovered.Count == 2 && new FileInfo(cachedImage).Length > 3,
            "Corrupt disk artwork is regenerated from the source files");

        var restoredTracks = cachedTracks.Select(track => new Track
        {
            FilePath = track.FilePath, Title = track.Title, Artist = track.Artist, Album = track.Album,
            Duration = track.Duration, FileSize = track.FileSize, LastWriteTimeUtcTicks = track.LastWriteTimeUtcTicks
        }).ToArray();
        var movedFiles = cachedTracks.Select(track => (Original: track.FilePath, Moved: track.FilePath + ".cache-test")).ToArray();
        foreach (var file in movedFiles) File.Move(file.Original, file.Moved);
        try
        {
            var persisted = await new PlaylistArtworkCache(cacheDirectory.Path).LoadAsync(restoredTracks);
            Check(persisted.Count == 2, "A new cache instance loads playlist covers after source files become unavailable");
        }
        finally
        {
            foreach (var file in movedFiles) File.Move(file.Moved, file.Original);
        }

        var firstVersion = await cache.LoadAsync([Track("versioned", red)]);
        var secondVersion = await cache.LoadAsync([Track("versioned", green)]);
        Check(!ReferenceEquals(firstVersion, secondVersion), "Changing in-memory artwork invalidates its cached playlist cover");

        var bounded = new PlaylistArtworkCache(Path.Combine(cacheDirectory.Path, "bounded"), entryLimit: 1);
        var evicted = await bounded.LoadAsync([Track("evicted", red)]);
        await bounded.LoadAsync([Track("retained", green)]);
        var reloaded = await bounded.LoadAsync([Track("evicted", red)]);
        Check(!ReferenceEquals(evicted, reloaded), "The in-memory playlist artwork cache enforces its entry limit");
    }

    private static Track Track(string name, byte[]? artwork, string? album = null) => new()
    { FilePath = name, Title = name, Album = album, Artist = "Test artist", ArtworkData = artwork };

    internal static byte[] Artwork(Color color, bool bmp = false)
    {
        var pixels = Enumerable.Range(0, 32 * 32).SelectMany(_ => new[] { color.B, color.G, color.R, color.A }).ToArray();
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        BitmapEncoder encoder = bmp ? new BmpBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }
}
