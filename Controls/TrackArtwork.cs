using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MusicPlayer.Models;

namespace MusicPlayer.Controls;

// Only realized rows request thumbnails. Weak keys release them with the tracks/data.
public sealed class TrackArtwork : Image
{
    private static readonly ConditionalWeakTable<byte[], Task<BitmapSource?>> Thumbnails = new();
    private static readonly ConditionalWeakTable<Track, Task<BitmapSource?>> FileThumbnails = new();

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Track), typeof(TrackArtwork),
        new PropertyMetadata(null, (control, _) => ((TrackArtwork)control).Refresh()));

    public Track? Track
    {
        get => (Track?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Task ArtworkReady { get; private set; } = Task.CompletedTask;
    private int _generation;

    private void Refresh() => ArtworkReady = LoadAsync();

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        Source = null;
        if (Track is not { } track) return;
        var image = await (track.ArtworkData is { Length: > 0 } data
            ? Thumbnails.GetValue(data, bytes => Task.Run(() => Decode(bytes)))
            : FileThumbnails.GetValue(track, item => Task.Run(() => LoadFromFile(item.FilePath))));
        if (Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (generation == _generation) Source = image;
        });
    }

    private static BitmapSource? LoadFromFile(string path)
    {
        try
        {
            // Saved playlists retain file paths but intentionally omit image bytes.
            using var file = TagLib.File.Create(path);
            var data = file.Tag.Pictures.FirstOrDefault()?.Data.Data;
            return data is { Length: > 0 } ? Decode(data) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 72;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}