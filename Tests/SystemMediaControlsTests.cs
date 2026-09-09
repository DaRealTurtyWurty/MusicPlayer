using System.Windows.Interop;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Media;
using Windows.Media.Control;

internal static partial class Program
{
    private static async Task CheckSystemMediaControlsAsync()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var controls = new FakeSystemMediaControls();
        using var binding = new SystemMediaControlsBinding(vm, controls, dispatcher);
        async Task Press(SystemMediaTransportControlsButton button)
        {
            // Exercise the actual threading boundary used by Windows callbacks.
            await Task.Run(() => controls.Press(button));
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        async Task Seek(TimeSpan position)
        {
            await Task.Run(() => controls.Seek(position));
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        Check(controls.State is { Track: null, CanPlay: false, CanNext: false, CanPrevious: false,
            Status: MediaPlaybackStatus.Closed }, "Empty player disables Windows media controls");
        await Press(SystemMediaTransportControlsButton.Play);
        Check(player.PlayCount == 0, "Media Play cannot start an empty queue");
        var a = new Track { FilePath = "Media A", Title = "Song A", Artist = "Artist A", Album = "Album A",
            ArtworkData = [1, 2, 3], Duration = TimeSpan.FromMinutes(3) };
        var b = Track("Media B");
        vm.Queue.Add(a);
        vm.Queue.Add(b);
        Check(controls.State is { CanPlay: true, CanNext: true }, "Queue-only session enables media Play");
        await Press(SystemMediaTransportControlsButton.Play);
        Check(vm.CurrentTrack == a && controls.State is { Track: var current, Status: MediaPlaybackStatus.Playing,
            CanPause: true, CanPrevious: true } && current == a && controls.State.Duration == player.Duration,
            "Media Play publishes complete track metadata and actual duration");
        Check(controls.UpdateThreadId == Environment.CurrentManagedThreadId,
            "Worker-thread media callbacks update playback on the WPF dispatcher");
        await Press(SystemMediaTransportControlsButton.Play);
        Check(player.PlayCount == 1, "Repeated media Play is idempotent");
        await Press(SystemMediaTransportControlsButton.Pause);
        await Press(SystemMediaTransportControlsButton.Pause);
        Check(!vm.IsPlaying && controls.State.Status == MediaPlaybackStatus.Paused,
            "Repeated media Pause never resumes playback");
        await Seek(TimeSpan.FromSeconds(42));
        Check(player.Position == TimeSpan.FromSeconds(42) && controls.State.Position == player.Position && !vm.IsPlaying,
            "System seek updates audio and metadata while retaining paused state");
        await Seek(TimeSpan.FromHours(1));
        Check(player.Position == player.Duration, "System seek clamps to duration");
        await Seek(TimeSpan.FromSeconds(-20));
        Check(player.Position == TimeSpan.Zero, "System seek clamps negative positions");
        await Press(SystemMediaTransportControlsButton.Next);
        Check(vm.CurrentTrack == b && controls.State.Track == b && !controls.State.CanNext,
            "Media Next advances once and replaces metadata including absent artwork");
        await Press(SystemMediaTransportControlsButton.Next);
        Check(vm.CurrentTrack == b && player.PlayCount == 2, "Disabled Next cannot replay or consume another track");
        await Press(SystemMediaTransportControlsButton.Previous);
        Check(vm.CurrentTrack == a && vm.Queue.Single() == b, "Media Previous uses playback history");
        vm.RepeatMode = PlaybackRepeatMode.One;
        player.End();
        Check(vm.CurrentTrack == a && controls.State.Status == MediaPlaybackStatus.Playing,
            "Natural repeat-one completion remains synchronized");
        vm.RepeatMode = PlaybackRepeatMode.Off;
        player.End();
        player.End();
        Check(controls.State.Status == MediaPlaybackStatus.Stopped, "Queue exhaustion publishes stopped status");
        await Press(SystemMediaTransportControlsButton.Play);
        await Press(SystemMediaTransportControlsButton.Stop);
        Check(controls.State.Status == MediaPlaybackStatus.Stopped && controls.State.Position == TimeSpan.Zero,
            "Media Stop resets timeline and publishes stopped status");
        vm.RepeatMode = PlaybackRepeatMode.All;
        Check(controls.State.CanNext, "Repeat-all enables Next even with no upcoming tracks");
        vm.SelectedTrack = Track("broken");
        vm.PlaySelectedTrackCommand.Execute(null);
        Check(controls.State is { Track: null, Status: MediaPlaybackStatus.Closed, Position: var position }
            && position == TimeSpan.Zero, "Failed loads clear stale Windows metadata and timeline");

        controls.ThrowOnUpdate = true;
        vm.SelectedTrack = a;
        vm.PlaySelectedTrackCommand.Execute(null);
        Check(vm.CurrentTrack == a && vm.IsPlaying && vm.PlaybackError is null,
            "A shell update failure cannot break audio playback");
        controls.ThrowOnUpdate = false;
        var count = player.PlayCount;
        controls.Press(SystemMediaTransportControlsButton.Next);
        binding.Dispose();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        controls.Press(SystemMediaTransportControlsButton.Play);
        vm.PauseCommand.Execute(null);
        Check(player.PlayCount == count && controls.DisposeCount == 1 && controls.SubscriberCount == 0,
            "Shutdown detaches callbacks and discards already queued media requests");
    }

    private static void CheckNativeSystemMediaControls()
    {
        // A real top-level HWND exercises WinRT activation without a visible window or audio device.
        using var source = new HwndSource(new HwndSourceParameters("MusicPlayer media integration test")
        {
            Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000)
        });
        using var controls = new WindowsSystemMediaControls(source.Handle);
        var native = SystemMediaTransportControlsInterop.GetForWindow(source.Handle);
        var track = new Track
        {
            FilePath = "Native media test", Title = "Test title", Artist = "Test artist", Album = "Test album",
            ArtworkData = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aE1cAAAAASUVORK5CYII=")
        };
        var state = new SystemMediaState(track, MediaPlaybackStatus.Playing, true, true, true, true,
            TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(3));
        controls.Update(state);
        var display = native.DisplayUpdater;
        Check(native.IsEnabled && native.PlaybackStatus == MediaPlaybackStatus.Playing && native.IsNextEnabled &&
            display.Type == MediaPlaybackType.Music && display.MusicProperties.Title == track.Title &&
            display.MusicProperties.Artist == track.Artist && display.MusicProperties.AlbumTitle == track.Album &&
            display.Thumbnail is not null, "Real Windows SMTC receives metadata, artwork and enabled controls");
        controls.Update(state with { Track = Track("No artwork"), Status = MediaPlaybackStatus.Paused, CanNext = false });
        Check(display.MusicProperties.Title == "No artwork" && display.MusicProperties.Artist == string.Empty &&
            display.MusicProperties.AlbumTitle == string.Empty && display.Thumbnail is null && !native.IsNextEnabled,
            "Real Windows SMTC clears metadata and artwork when the next track lacks them");
        controls.Dispose();
        Check(!native.IsEnabled && native.PlaybackStatus == MediaPlaybackStatus.Closed &&
            display.Type == MediaPlaybackType.Unknown, "Real Windows SMTC disables and clears its session on disposal");
    }

    private static async Task CheckSystemMediaSessionAsync()
    {
        using var source = new HwndSource(new HwndSourceParameters("MusicPlayer media session smoke test")
        {
            Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000)
        });
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        using var binding = new SystemMediaControlsBinding(vm, new WindowsSystemMediaControls(source.Handle),
            Dispatcher.CurrentDispatcher);
        var a = Track($"Media session smoke {Guid.NewGuid():N}");
        var b = Track("Media session next");
        vm.Queue.Add(a);
        vm.Queue.Add(b);
        vm.PlayCommand.Execute(null);
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        GlobalSystemMediaTransportControlsSession? session = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session is null && DateTime.UtcNow < deadline)
        {
            foreach (var candidate in manager.GetSessions())
            {
                if ((await candidate.TryGetMediaPropertiesAsync()).Title == a.Title)
                {
                    session = candidate;
                    break;
                }
            }
            if (session is null) await Task.Delay(100);
        }
        Check(session is not null, "Windows exposes the app as a system media session");
        async Task WaitFor(Func<bool> predicate, string message)
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!predicate() && DateTime.UtcNow < until) await Task.Delay(50);
            Check(predicate(), message);
        }
        Check(await session!.TryPauseAsync(), "Windows accepts a session Pause request");
        await WaitFor(() => !vm.IsPlaying, "Windows session Pause reaches the app's audio player");
        Check(await session.TryPlayAsync(), "Windows accepts a session Play request");
        await WaitFor(() => vm.IsPlaying, "Windows session Play resumes playback");
        await WaitFor(() => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Windows receives the updated playing status");
        Check(await session.TryTogglePlayPauseAsync(), "Windows accepts the media-key play/pause toggle");
        await WaitFor(() => !vm.IsPlaying, "Windows media-key toggle pauses once");
        await WaitFor(() => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Windows receives the updated paused status");
        Check(await session.TryTogglePlayPauseAsync(), "Windows accepts a second media-key toggle");
        await WaitFor(() => vm.IsPlaying, "Windows media-key toggle resumes once");
        Check(await session.TrySkipNextAsync(), "Windows accepts a session Next request");
        await WaitFor(() => vm.CurrentTrack == b, "Windows session Next advances the queue exactly once");
        Check(await session.TrySkipPreviousAsync(), "Windows accepts a session Previous request");
        await WaitFor(() => vm.CurrentTrack == a, "Windows session Previous follows playback history");
        Check(await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(30).Ticks),
            "Windows accepts a session seek request");
        await WaitFor(() => player.Position == TimeSpan.FromSeconds(30), "Windows session seek reaches the audio player");
    }

    private sealed class FakeSystemMediaControls : ISystemMediaControls
    {
        public event Action<SystemMediaTransportControlsButton>? ButtonPressed;
        public event Action<TimeSpan>? PositionRequested;
        public SystemMediaState State { get; private set; } = null!;
        public int UpdateThreadId { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ThrowOnUpdate { get; set; }
        public int SubscriberCount => (ButtonPressed?.GetInvocationList().Length ?? 0) +
            (PositionRequested?.GetInvocationList().Length ?? 0);
        public void Press(SystemMediaTransportControlsButton button) => ButtonPressed?.Invoke(button);
        public void Seek(TimeSpan position) => PositionRequested?.Invoke(position);
        public void Update(SystemMediaState state)
        {
            if (ThrowOnUpdate) throw new InvalidOperationException("Shell unavailable");
            State = state;
            UpdateThreadId = Environment.CurrentManagedThreadId;
        }
        public void Dispose() => DisposeCount++;
    }
}
