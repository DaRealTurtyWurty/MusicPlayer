using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckDiscordPresenceAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerDiscordTests");
        var preferencesPath = System.IO.Path.Combine(temporary.Path, "preferences.json");
        var preferences = new JsonUiPreferencesStore(preferencesPath);
        Check(!preferences.LoadDiscordPresence().Enabled &&
              preferences.LoadDiscordPresence().ApplicationId == "1549010425205497987",
            "Discord presence defaults to off with the supplied application ID");
        preferences.SaveVolume(42, 42);
        preferences.SaveDiscordPresence(new(true, "123456789012345678"));
        preferences.SaveLyricsEnabled(false);
        Check(preferences.LoadDiscordPresence() == new DiscordPresenceOptions(true, "123456789012345678") &&
              preferences.LoadVolume().Volume == 42, "Discord preferences round-trip without overwriting other settings");
        System.IO.File.WriteAllText(preferencesPath, "{\"Volume\":35}");
        Check(!preferences.LoadDiscordPresence().Enabled && preferences.LoadVolume().Volume == 35 &&
              preferences.LoadDiscordPresence().ApplicationId == DiscordPresenceOptions.DefaultApplicationId,
            "Old preferences load with presence disabled and the built-in application ID");
        Check(new[] { "", "-1", "0", "1e18", "１２３", "18446744073709551616" }
            .All(id => !DiscordPresenceOptions.IsValidApplicationId(id)), "Invalid application IDs are rejected locally");

        var dispatcher = Dispatcher.CurrentDispatcher;
        var player = new FakePlayer();
        var clock = new PresenceClock();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            uiPreferencesStore: preferences);
        var clients = new List<FakeDiscordClient>();
        using var binding = new DiscordPresenceBinding(vm, dispatcher, id =>
        {
            var client = new FakeDiscordClient(id);
            clients.Add(client);
            return client;
        }, clock);
        async Task Drain(bool advance = true)
        {
            if (advance) clock.Advance(TimeSpan.FromSeconds(2));
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        async Task Pump()
        {
            await Task.Delay(350);
            await Drain(false);
        }

        var song = new Track { FilePath = "Discord A", Title = "Song A", Artist = "Artist A", Album = "Album A" };
        vm.SelectedTrack = song;
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain();
        Check(clients.Count == 0, "Playing with presence disabled never creates a Discord client");
        vm.ConfigureDiscordPresence(new(true, " 123456789012345678 "));
        await Drain();
        var first = clients.Single();
        Check(first.InitializeCount == 1 && first.ApplicationId == "123456789012345678" && first.Published.Count == 0,
            "Enabling initializes once and stores presence while Discord is offline");
        vm.SelectedTrack = Track("Skipped offline");
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain();
        vm.SelectedTrack = song;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.PositionSeconds = 42;
        await Drain();
        first.ConnectOnPump = true;
        await Pump();
        var presence = first.Published.Single()!;
        Check(presence.Title == "Song A" && presence.ArtistAndAlbum == "Artist A — Album A" &&
              presence.Start == clock.GetUtcNow().AddSeconds(-42) &&
              presence.End == clock.GetUtcNow().AddSeconds(138),
            "Connection publishes only the latest song with audio position and actual duration");
        Check(vm.DiscordPresenceStatus == "Connected", "Connection status reaches the view model");

        var publishedCount = first.Published.Count;
        player.Seek(TimeSpan.FromSeconds(43));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Pump();
        Check(vm.PositionSeconds == 43 && first.Published.Count == publishedCount,
            "Normal position timer ticks never republish presence");
        vm.PositionSeconds = 60;
        await Drain();
        Check(first.Published[^1]!.Start == clock.GetUtcNow().AddSeconds(-60), "Seeking recalculates timestamps");
        publishedCount = first.Published.Count;
        vm.PositionSeconds = 61;
        await Drain(false);
        vm.PositionSeconds = 65;
        await Drain(false);
        Check(first.Published.Count == publishedCount, "Rapid seeks are throttled");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Pump();
        Check(first.Published.Count == publishedCount + 1 &&
              first.Published[^1]!.Start == clock.GetUtcNow().AddSeconds(-65), "Throttled seek publishes the latest position");

        vm.PauseCommand.Execute(null);
        await Drain(false);
        Check(first.Published[^1] is null, "Pause clears presence immediately even within the throttle window");
        clock.Advance(TimeSpan.FromMinutes(5));
        vm.PlayCommand.Execute(null);
        await Drain(false);
        Check(first.Published[^1]!.Start == clock.GetUtcNow().AddSeconds(-65), "Resume excludes paused time");
        vm.SelectedTrack = Track("Intermediate");
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.SelectedTrack = Track("Final");
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain();
        Check(first.Published[^1] is { Title: "Final", ArtistAndAlbum: "" },
            "Rapid track changes coalesce and clear missing metadata");

        first.Disconnect();
        vm.SelectedTrack = Track("After restart");
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain(false);
        publishedCount = first.Published.Count;
        first.ConnectOnPump = true;
        await Pump();
        Check(first.Published.Count == publishedCount + 1 && first.Published[^1]!.Title == "After restart",
            "Discord reconnect receives the latest snapshot even inside the update throttle");
        vm.StopCommand.Execute(null);
        await Drain(false);
        Check(first.Published[^1] is null, "Stop clears presence");
        first.Disconnect();
        first.ConnectOnPump = true;
        await Pump();
        Check(first.Published[^1] is null, "Reconnect while stopped cannot revive an old song");

        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Shutdown = closing.Task;
        vm.ConfigureDiscordPresence(new(true, "987654321098765432"));
        await Drain();
        Check(clients.Count == 1 && first.DisposeCount == 1, "Replacement waits for the old session to finish clearing");
        closing.SetResult();
        await Pump();
        Check(first.DisposeCount == 1 && first.SubscriberCount == 0 && clients.Count == 2 &&
              clients[^1].ApplicationId == "987654321098765432", "Changing IDs disposes the old client and detaches callbacks");
        var second = clients[^1];
        second.ConnectOnPump = true;
        await Pump();
        second.ThrowOnUpdate = true;
        vm.PlayCommand.Execute(null);
        await Drain();
        Check(vm.IsPlaying && vm.PlaybackError is null && second.DisposeCount == 1,
            "Discord failures cannot turn successful audio playback into a playback error");
        clock.Advance(TimeSpan.FromSeconds(10));
        await Pump();
        Check(clients.Count == 3 && clients[^1].Desired?.Title == "After restart", "Unexpected client failure retries with current playback");

        var third = clients[^1];
        third.ConnectOnPump = true;
        await Pump();
        vm.RepeatMode = PlaybackRepeatMode.One;
        player.End();
        await Drain();
        Check(third.Published[^1]!.Start == clock.GetUtcNow(), "Repeat-one starts a fresh timer for the same track");
        vm.RepeatMode = PlaybackRepeatMode.Off;
        player.End();
        await Drain();
        Check(third.Published[^1] is null, "Queue exhaustion clears presence");
        vm.SelectedTrack = Track("broken");
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain();
        Check(third.Desired is null, "Failed audio loads retain no stale presence");
        vm.ConfigureDiscordPresence(new(false, "987654321098765432"));
        await Drain();
        Check(third.DisposeCount == 1 && third.SubscriberCount == 0 && vm.DiscordPresenceStatus == "Off",
            "Disabling clears, disconnects and removes callbacks");
        vm.ConfigureDiscordPresence(new(true, "987654321098765432"));
        vm.SelectedTrack = song;
        vm.PlaySelectedTrackCommand.Execute(null);
        await Drain();
        var last = clients[^1];
        vm.PositionSeconds = 12; // Leave a dispatcher callback queued during shutdown.
        binding.Dispose();
        binding.Dispose();
        await Drain();
        Check(last.DisposeCount == 1 && last.Desired is null && last.SubscriberCount == 0,
            "Shutdown is idempotent, clears presence and cancels queued updates");

        var activity = DiscordPresenceClient.CreateActivity(new(string.Concat(Enumerable.Repeat("🎵", 80)),
            "Artist\nAlbum", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(3)));
        Check(activity.Type == DiscordRPC.ActivityType.Listening && Encoding.UTF8.GetByteCount(activity.Details) == 128 &&
              !activity.Details.Contains('\uFFFD') && activity.State == "Artist Album" && activity.Assets is null &&
              activity.Timestamps.End > activity.Timestamps.Start, "RPC payload has listening type, safe Unicode text and timestamps without artwork");
        activity = DiscordPresenceClient.CreateActivity(new(" ", null, null, null));
        Check(activity.Details == "Unknown track" && activity.State is null && activity.Timestamps is null,
            "Empty metadata and unknown duration create a valid text-only payload");
    }

    private sealed class PresenceClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeDiscordClient(string applicationId) : IDiscordPresenceClient
    {
        public string ApplicationId => applicationId;
        public event Action<string>? StatusChanged;
        public bool IsConnected { get; private set; }
        public Task Shutdown { get; set; } = Task.CompletedTask;
        public bool ConnectOnPump { get; set; }
        public bool ThrowOnUpdate { get; set; }
        public int InitializeCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SubscriberCount => StatusChanged?.GetInvocationList().Length ?? 0;
        public DiscordPresence? Desired { get; private set; }
        public List<DiscordPresence?> Published { get; } = [];
        public void Initialize() => InitializeCount++;
        public void Update(DiscordPresence? presence)
        {
            if (ThrowOnUpdate) throw new InvalidOperationException("Discord unavailable");
            Desired = presence;
            if (IsConnected) Published.Add(presence);
        }
        public void Disconnect() => IsConnected = false;
        public void Pump()
        {
            if (!ConnectOnPump) return;
            ConnectOnPump = false;
            IsConnected = true;
            Published.Add(Desired);
            StatusChanged?.Invoke("Connected");
        }
        public void Dispose() => DisposeCount++;
    }

    private static void CheckDiscordSettingsLayout()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        var settings = new MusicPlayer.Views.DiscordSettingsWindow(vm);
        var content = (FrameworkElement)settings.Content;
        content.Measure(new Size(460, double.PositiveInfinity));
        content.Arrange(new Rect(new Point(), content.DesiredSize));
        content.UpdateLayout();
        Check(content.ActualHeight < 600 && ((TextBox)settings.FindName("ApplicationIdBox")).ActualWidth > 300,
            "Discord settings compile and fit their dialog");
        Check(!vm.DiscordPresenceOptions.Enabled, "Opening settings never enables sharing");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(460, (int)Math.Ceiling(content.DesiredSize.Height),
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(settings.Background, null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts");
        using (var stream = System.IO.File.Create("artifacts/discord-settings.png")) encoder.Save(stream);
        settings.Close();
    }
}
