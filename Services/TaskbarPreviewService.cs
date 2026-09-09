using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using MusicPlayer.Controls;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Services;

/// <summary>Native thumbnail toolbar plus a DWM now-playing bitmap, independent of SMTC.</summary>
public sealed class TaskbarPreviewService : IDisposable
{
    private const int ForceIconicRepresentation = 7, HasIconicBitmap = 10;
    private const int SendIconicThumbnail = 0x0323, SendIconicLivePreview = 0x0326;
    private readonly Window _window;
    private readonly MainViewModel _player;
    private readonly HwndSource _source;
    private readonly nint _handle;
    private readonly TrackArtwork _artwork = new();
    private readonly ThumbButtonInfo _playPause;
    private readonly ThumbButtonInfo _stop;
    private readonly ImageSource _playImage, _pauseImage;
    private readonly string _originalTitle;
    private bool _disposed;
    private int _artworkGeneration;
    private int _lastPositionSecond = -1;

    public TaskbarPreviewService(Window window, MainViewModel player)
    {
        _window = window;
        _player = player;
        _originalTitle = window.Title;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("The window needs an HWND first.");
        _playImage = Icon("IconPlay");
        _pauseImage = Icon("IconPause");
        _playPause = Button("Play", _playImage, player.TogglePlaybackCommand);
        _stop = Button("Stop", Icon(null), player.StopCommand);
        window.TaskbarItemInfo = new TaskbarItemInfo
        {
            ThumbButtonInfos = new ThumbButtonInfoCollection
            {
                Button("Previous", Icon("IconSkipBack"), player.PreviousCommand),
                _playPause,
                Button("Next", Icon("IconSkipForward"), player.NextCommand),
                _stop
            }
        };
        _source.AddHook(WindowProc);
        _player.PropertyChanged += OnPropertyChanged;
        // Keep native buttons working even if custom thumbnails aren't available (e.g. remote desktop).
        var enabled = 1;
        var result = DwmSetWindowAttribute(_handle, ForceIconicRepresentation, ref enabled, sizeof(int));
        if (result >= 0) result = DwmSetWindowAttribute(_handle, HasIconicBitmap, ref enabled, sizeof(int));
        if (result < 0)
        {
            enabled = 0;
            DwmSetWindowAttribute(_handle, ForceIconicRepresentation, ref enabled, sizeof(int));
            Trace.TraceWarning($"Custom taskbar thumbnails are unavailable: 0x{result:X8}");
        }
        Update();
        RefreshArtwork();
    }

    private static ThumbButtonInfo Button(string description, ImageSource image, ICommand command) => new()
    {
        Description = description, ImageSource = image, Command = command, DismissWhenClicked = false
    };

    private ImageSource Icon(string? key)
    {
        var geometry = key is null ? new RectangleGeometry(new Rect(5, 5, 14, 14))
            : (Geometry)_window.FindResource(key);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentTrack)) RefreshArtwork();
        if (e.PropertyName == nameof(MainViewModel.PositionSeconds))
        {
            var second = (int)_player.PositionSeconds;
            if (second == _lastPositionSecond) return;
            _lastPositionSecond = second;
            Invalidate();
        }
        else if (e.PropertyName is nameof(MainViewModel.CurrentTrack) or nameof(MainViewModel.IsPlaying)
            or nameof(MainViewModel.IsPlaybackStopped) or nameof(MainViewModel.DurationSeconds)) Update();
    }

    private async void RefreshArtwork()
    {
        var generation = ++_artworkGeneration;
        _artwork.Track = _player.CurrentTrack;
        Invalidate();
        try
        {
            await _artwork.ArtworkReady;
            if (!_disposed && generation == _artworkGeneration) Invalidate();
        }
        catch (Exception ex) { Trace.TraceWarning($"Could not load taskbar artwork: {ex.Message}"); }
    }

    private void Update()
    {
        var track = _player.CurrentTrack;
        _window.Title = track is null ? _originalTitle : string.IsNullOrWhiteSpace(track.Artist)
            ? $"{track.Title} — {_originalTitle}" : $"{track.Artist} — {track.Title} — {_originalTitle}";
        _window.TaskbarItemInfo.Description = _window.Title;
        _playPause.Description = _player.PlayPauseLabel;
        _playPause.ImageSource = _player.IsPlaying ? _pauseImage : _playImage;
        _stop.IsEnabled = track is not null;
        Invalidate();
    }

    private void Invalidate()
    {
        if (!_disposed) DwmInvalidateIconicBitmaps(_handle);
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed || message is not (SendIconicThumbnail or SendIconicLivePreview)) return 0;
        try
        {
            BitmapSource image;
            if (message == SendIconicThumbnail)
            {
                var dimensions = (long)lParam;
                var width = (int)((dimensions >> 16) & 0xffff);
                var height = (int)(dimensions & 0xffff);
                if (width == 0 || height == 0) return 0;
                image = TaskbarPreviewRenderer.Render(_player, _artwork.Source, width, height);
            }
            else
            {
                // Peek should still show the full application, including when it is minimized.
                if (_window.Content is not FrameworkElement content || content.ActualWidth < 1 || content.ActualHeight < 1)
                    return 0;
                var dpi = VisualTreeHelper.GetDpi(_window);
                var scale = Math.Min(dpi.DpiScaleX, 4096 / Math.Max(content.ActualWidth, content.ActualHeight));
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(content), null,
                        new Rect(0, 0, content.ActualWidth * scale, content.ActualHeight * scale));
                var preview = new RenderTargetBitmap(Math.Max(1, (int)(content.ActualWidth * scale)),
                    Math.Max(1, (int)(content.ActualHeight * scale)), 96, 96, PixelFormats.Pbgra32);
                preview.Render(visual);
                image = preview;
            }
            WithBitmap(image, bitmap => Marshal.ThrowExceptionForHR(message == SendIconicThumbnail
                ? DwmSetIconicThumbnail(hwnd, bitmap, 0) : DwmSetIconicLivePreviewBitmap(hwnd, bitmap, 0, 1)));
            handled = true;
        }
        catch (Exception ex) { Trace.TraceWarning($"Could not render taskbar preview: {ex.Message}"); }
        return 0;
    }

    private static void WithBitmap(BitmapSource image, Action<nint> action)
    {
        // DWM takes a copy. Release our 32-bit top-down DIB immediately after the native call.
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = image.PixelWidth, Height = -image.PixelHeight,
            Planes = 1, BitCount = 32
        };
        var bitmap = CreateDIBSection(0, ref header, 0, out var pixels, 0, 0);
        if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var stride = image.PixelWidth * 4;
            image.CopyPixels(Int32Rect.Empty, pixels, stride * image.PixelHeight, stride);
            action(bitmap);
        }
        finally { DeleteObject(bitmap); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPropertyChanged;
        _source.RemoveHook(WindowProc);
        _artwork.Track = null;
        _window.TaskbarItemInfo = null;
        _window.Title = _originalTitle;
        var disabled = 0;
        DwmSetWindowAttribute(_handle, HasIconicBitmap, ref disabled, sizeof(int));
        DwmSetWindowAttribute(_handle, ForceIconicRepresentation, ref disabled, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmInvalidateIconicBitmaps(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicThumbnail(nint hwnd, nint bitmap, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicLivePreviewBitmap(nint hwnd, nint bitmap, nint offset, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint hdc,
        ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
}
