using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static void CheckDatabaseRecovery()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerRecoveryTests");
        var path = Path.Combine(temporary.Path, "music.db");
        var store = new SqliteMusicStore(path);
        var song = new Track { FilePath = Path.Combine(temporary.Path, "song.flac"), Title = "Original", ExplicitlyAddedToLibrary = true };
        store.SaveLibrary([song]);
        var playlist = new Playlist([song, song]) { Name = "Favourites" };
        store.SavePlaylists([playlist]);
        ((IPlaybackSessionStore)store).SaveSession(new PlaybackSession(song, TimeSpan.FromSeconds(12), [song]));

        // Keep a WAL connection open to prove snapshots include committed sidecar data.
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; UPDATE Tracks SET Title='WAL title'";
            command.ExecuteNonQuery();
            var backup = DatabaseRecovery.Backup(path);
            Check(new SqliteMusicStore(backup).LoadLibrary().Single().Title == "WAL title", "Online snapshot includes committed WAL changes");
        }
        var snapshot = DatabaseRecovery.Backup(path);
        SqliteConnection.ClearAllPools();
        File.WriteAllText(path, "damaged database");
        var corrupt = File.ReadAllBytes(path);
        var badBackup = Path.Combine(temporary.Path, "invalid.db");
        File.WriteAllText(badBackup, "not a database");
        var rejected = false;
        try { DatabaseRecovery.Restore(badBackup, path); }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException) { rejected = true; }
        Check(rejected && corrupt.SequenceEqual(File.ReadAllBytes(path)), "Invalid restore leaves original database untouched");
        var archive = DatabaseRecovery.Restore(snapshot, path);
        Check(corrupt.SequenceEqual(File.ReadAllBytes(Path.Combine(archive, "music.db"))), "Recovery preserves corrupt database for investigation");
        var restored = new SqliteMusicStore(path);
        Check(restored.LoadLibrary().Single().Title == "WAL title" && restored.LoadPlaylists().Single().Tracks.Count == 2,
            "Recovery restores library and duplicate playlist entries");
        Check(((IPlaybackSessionStore)restored).LoadSession().Position == TimeSpan.FromSeconds(12), "Recovery preserves playback position");
        for (var i = 0; i < 9; i++) DatabaseRecovery.Backup(path, "startup");
        Check(Directory.GetFiles(Path.Combine(temporary.Path, "backups"), "*-startup.db").Length == 7 && File.Exists(snapshot),
            "Routine backups are bounded and manual snapshots survive pruning");
    }

    private static void CheckDiagnosticLogs()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerLogTests");
        for (var i = 0; i < 12; i++)
        {
            using var log = new DiagnosticLog(temporary.Path);
            log.WriteLine("test diagnostic");
            for (var j = 0; j < 300; j++) log.WriteLine(new string('x', 8192));
        }
        var files = Directory.GetFiles(temporary.Path, "*.log");
        Check(files.Length == 10 && files.All(f => new FileInfo(f).Length < 2 * 1024 * 1024 + 16384),
            "Diagnostics bound session retention and file size");
        Check(files.All(f => File.ReadAllText(f).Contains("test diagnostic")), "Diagnostic entries persist without requiring flush");
    }

    private static void CheckReleaseAccessibility()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        window.Measure(new Size(1200, 760));
        window.Arrange(new Rect(0, 0, 1200, 760));
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        foreach (var name in new[] { "VolumeSlider", "SeekSlider", "MuteButton" })
        {
            var control = (FrameworkElement)window.FindName(name);
            var peer = UIElementAutomationPeer.CreatePeerForElement(control);
            Check(peer is not null && !string.IsNullOrWhiteSpace(peer.GetName()) && control.Focusable,
                $"{name} has a screen-reader name and supports keyboard focus");
        }
        var settings = new SettingsView { DataContext = vm };
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var size = new Size(1920 / scale, 1080 / scale);
            settings.Measure(size);
            settings.Arrange(new Rect(size));
            settings.UpdateLayout();
            var scroll = Descendants(settings).OfType<ScrollViewer>().First();
            var output = (ComboBox)settings.FindName("AudioOutputSelector");
            Check(output.ActualWidth <= size.Width && output.FocusVisualStyle is not null &&
                  AutomationProperties.GetName(output) == "Audio output device" && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                $"Settings remains scrollable and output selector fits at {scale * 100}% equivalent logical size");
        }
        var queue = new QueuePanel { DataContext = vm };
        var list = (ListBox)queue.FindName("QueueList");
        Check(AutomationProperties.GetName(list) == "Playback queue and history" &&
              list.InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.Up && k.Modifiers == ModifierKeys.Alt),
            "Queue has an accessible name and keyboard reorder binding");
        var dialog = (FrameworkElement)window.FindName("ClearQueueDialog");
        Check(KeyboardNavigation.GetTabNavigation(dialog) == KeyboardNavigationMode.Cycle,
            "Queue confirmation traps tab navigation within the dialog");
        window.Close();
    }
}
