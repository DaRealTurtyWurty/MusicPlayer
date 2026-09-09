using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckTaskbarPreviewAsync()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var window = new Window { Title = "Music Player", Width = 900, Height = 560 };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        using var messages = new StringWriter();
        using var listener = new TextWriterTraceListener(messages);
        Trace.Listeners.Add(listener);
        using var preview = new TaskbarPreviewService(window, vm);
        var buttons = window.TaskbarItemInfo.ThumbButtonInfos;
        Check(buttons.Select(b => b.Description).SequenceEqual(new[] { "Previous", "Play", "Next", "Stop" }),
            "Taskbar toolbar presents Previous, Play/Pause, Next and Stop");
        Check(!buttons[0].IsEnabled && !buttons[1].IsEnabled && !buttons[2].IsEnabled && !buttons[3].IsEnabled,
            "Taskbar buttons disable unavailable actions when empty");
        Check(buttons.All(b => b.ImageSource is not null && !b.DismissWhenClicked),
            "Taskbar buttons have icons and keep the preview open after clicks");
        listener.Flush();
        Check(!messages.ToString().Contains("taskbar", StringComparison.OrdinalIgnoreCase),
            "Real HWND accepts DWM custom thumbnail setup");
        Trace.Listeners.Remove(listener);
        SaveTaskbarImage(TaskbarPreviewRenderer.Render(vm, null, 300, 150), "taskbar-empty.png");

        vm.Queue.Add(Track("A Walk"));
        vm.Queue.Add(Track("Awake"));
        Check(buttons[1].IsEnabled && buttons[2].IsEnabled, "Queue enables taskbar Play and Next");
        buttons[1].Command.Execute(null);
        var pauseIcon = buttons[1].ImageSource;
        Check(vm.IsPlaying && buttons[1].Description == "Pause" && buttons[0].IsEnabled && buttons[3].IsEnabled &&
            window.Title.Contains("A Walk") && window.TaskbarItemInfo.Description == window.Title,
            "Taskbar Play starts playback, switches to Pause, and updates the window's taskbar title");
        buttons[1].Command.Execute(null);
        Check(!vm.IsPlaying && buttons[1].Description == "Play" && !ReferenceEquals(buttons[1].ImageSource, pauseIcon),
            "Taskbar Pause updates its label and image");
        buttons[2].Command.Execute(null);
        Check(vm.CurrentTrack?.Title == "Awake" && !buttons[2].IsEnabled, "Taskbar Next advances and disables at queue end");
        buttons[0].Command.Execute(null);
        Check(vm.CurrentTrack?.Title == "A Walk", "Taskbar Previous follows playback history");
        vm.PositionSeconds = 123;
        buttons[3].Command.Execute(null);
        Check(vm.IsPlaybackStopped && vm.PositionSeconds == 0, "Taskbar Stop resets playback");

        vm.CurrentTrack = new MusicPlayer.Models.Track
        {
            FilePath = "Taskbar fixture", Title = "A Walk", Artist = "Tycho", Album = "Dive"
        };
        vm.IsPlaying = true;
        vm.DurationSeconds = 295;
        vm.PositionSeconds = 123;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(29, 64, 73), Color.FromRgb(232, 171, 110), 90),
            null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(226, 111, 83)), null,
            new EllipseGeometry(new Point(50, 48), 29, 29)));
        var artwork = new DrawingImage(drawing);
        var normal = TaskbarPreviewRenderer.Render(vm, artwork, 300, 150);
        SaveTaskbarImage(normal, "taskbar-playing.png");
        foreach (var (width, height) in new[] { (200, 120), (600, 300), (80, 30), (1, 1) })
        {
            var bitmap = TaskbarPreviewRenderer.Render(vm, artwork, width, height);
            Check(bitmap.PixelWidth <= width && bitmap.PixelHeight <= height && bitmap.Format == PixelFormats.Pbgra32,
                $"Taskbar bitmap obeys DWM's {width}x{height} bounds with 32-bit pixels");
        }
        SaveTaskbarImage(TaskbarPreviewRenderer.Render(vm, artwork, 200, 120), "taskbar-playing-small.png");
        vm.CurrentTrack = new MusicPlayer.Models.Track
        {
            FilePath = "Long taskbar title", Title = "A very long song title that should end with an ellipsis instead of overlapping",
            Artist = "An artist name longer than the available thumbnail width", Album = "An album with a long name too"
        };
        vm.PauseCommand.Execute(null);
        SaveTaskbarImage(TaskbarPreviewRenderer.Render(vm, null, 300, 150), "taskbar-long-title.png");
        vm.CurrentTrack = null;
        Check(window.Title == "Music Player" && !buttons[3].IsEnabled,
            "Clearing the track restores the taskbar title and disables Stop");
        preview.Dispose();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(window.TaskbarItemInfo is null,
            "Disposal removes the toolbar and ignores pending artwork");
        vm.CurrentTrack = Track("After disposal");
        Check(window.Title == "Music Player", "Disposed taskbar preview no longer observes playback changes");
        window.Close();
    }

    private static void SaveTaskbarImage(BitmapSource bitmap, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }

}
