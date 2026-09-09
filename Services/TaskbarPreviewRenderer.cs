using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Services;

public static class TaskbarPreviewRenderer
{
    public static BitmapSource Render(MainViewModel player, ImageSource? artwork, int maxWidth, int maxHeight)
    {
        const double width = 300, height = 150;
        var scale = Math.Min(Math.Clamp(maxWidth, 1, 2048) / width, Math.Clamp(maxHeight, 1, 2048) / height);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(scale, scale));
            var background = Brush("CanvasBrush", "#1B1B1B");
            var surface = Brush("RaisedBrush", "#2D2D2D");
            var foreground = Brush("TextBrush", "#E5E5E5");
            var muted = Brush("MutedBrush", "#A8A8A8");
            var accent = Brush("AccentBrush", "#B7C9DC");
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
            var cover = new Rect(12, 12, 80, 80);
            drawing.DrawRoundedRectangle(surface, null, cover, 4, 4);
            if (artwork is not null)
            {
                var imageBrush = new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
                drawing.DrawRoundedRectangle(imageBrush, null, cover, 4, 4);
            }
            else
            {
                Text("♪", 35, 24, 36, 50, muted);
            }
            var track = player.CurrentTrack;
            Text(track?.Title ?? "Nothing playing", 104, 11, 16, 184, foreground, true);
            Text(track is null ? "Choose a song to get started" : track.Artist ?? "Unknown artist",
                104, 37, 13, 184, muted);
            Text(track?.Album ?? string.Empty, 104, 59, 12, 184, muted);
            var status = track is null ? "READY" : player.IsPlaying ? "PLAYING"
                : player.IsPlaybackStopped ? "STOPPED" : "PAUSED";
            Text(status, 12, 111, 10, 100, accent, true);
            var duration = double.IsFinite(player.DurationSeconds) ? Math.Max(0, player.DurationSeconds) : 0;
            var position = double.IsFinite(player.PositionSeconds) ? Math.Clamp(player.PositionSeconds, 0, duration) : 0;
            Text($"{Format(position)} / {Format(duration)}", 150, 108, 12, 138, muted, right: true);
            drawing.DrawRoundedRectangle(surface, null, new Rect(12, 134, 276, 3), 1.5, 1.5);
            if (duration > 0 && position > 0)
                drawing.DrawRoundedRectangle(accent, null, new Rect(12, 134, 276 * position / duration, 3), 1.5, 1.5);
            drawing.Pop();

            void Text(string value, double x, double y, double size, double availableWidth, Brush brush,
                bool bold = false, bool right = false)
            {
                var text = new FormattedText(value.ReplaceLineEndings(" "), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                        bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, 1)
                {
                    MaxTextWidth = availableWidth,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = right ? TextAlignment.Right : TextAlignment.Left
                };
                drawing.DrawText(text, new Point(x, y));
            }
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Brush(string resource, string fallback) => Application.Current?.TryFindResource(resource) as Brush
        ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;

    private static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
