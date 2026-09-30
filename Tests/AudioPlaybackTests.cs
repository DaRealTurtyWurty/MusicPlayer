using System.IO;
using MusicPlayer;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using NAudio.Wave;

internal static partial class Program
{
    private static readonly string[] AudioFormats = ["wav", "mp3", "flac", "m4a", "ogg"];
    private static string AudioFixture(string extension) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Playback", "silence." + extension);

    private static async Task CheckAudioAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerAudioTests");
        foreach (var extension in AudioFormats)
        {
            var backend = new TestAudioBackend();
            using var player = new NAudioPlayer(backend);
            var ended = 0;
            player.PlaybackEnded += (_, _) => ended++;
            player.Volume = .37f;
            player.Load(AudioFixture(extension));
            Check(player.Duration.TotalSeconds is > 1.9 and < 2.2, $"{extension}: real decoder duration");
            player.Play();
            backend.Last!.ReadAhead(TimeSpan.FromSeconds(.8));
            backend.Last.RenderedPosition = TimeSpan.FromSeconds(.25);
            Check(Math.Abs(player.PresentationPosition.TotalSeconds - .25) < .001 && player.Position.TotalSeconds > .7,
                $"{extension}: presentation excludes queued samples");
            var obsolete = backend.Last;
            var lateCallback = obsolete.CaptureStoppedCallback();
            player.Seek(TimeSpan.FromSeconds(.5));
            lateCallback?.Invoke(obsolete, new StoppedEventArgs());
            Check(ended == 0 && obsolete.Disposed && player.IsOutputPlaying && Math.Abs(player.Position.TotalSeconds - .5) < .05,
                $"{extension}: playing seek flushes output and ignores stale completion");
            player.Pause();
            player.Seek(TimeSpan.FromSeconds(1));
            Check(!player.IsOutputPlaying && Math.Abs(player.Position.TotalSeconds - 1) < .05,
                $"{extension}: paused seek stays paused");
            player.Seek(TimeSpan.FromSeconds(-1));
            Check(player.Position == TimeSpan.Zero, $"{extension}: seek clamps below zero");
            player.Seek(TimeSpan.FromSeconds(999));
            Check(Math.Abs((player.Position - player.Duration).TotalSeconds) < .05, $"{extension}: seek clamps to duration");
            player.Play();
            backend.Last!.Drain();
            backend.Last.End();
            backend.Last.End();
            Check(ended == 1 && !player.IsOutputPlaying && player.PresentationPosition == player.Duration,
                $"{extension}: end-of-track signalled exactly once");
            player.Stop();
            Check(player.Position == TimeSpan.Zero && ended == 1, $"{extension}: stop resets without advancing");

            var missing = Path.Combine(temporary.Path, "missing." + extension);
            ExpectAudioLoadFailure(player, missing, $"{extension}: missing file");
            var corrupt = Path.Combine(temporary.Path, "corrupt." + extension);
            File.WriteAllText(corrupt, "invalid audio data");
            ExpectAudioLoadFailure(player, corrupt, $"{extension}: corrupt file");
            var empty = Path.Combine(temporary.Path, "empty." + extension);
            File.WriteAllBytes(empty, []);
            ExpectAudioLoadFailure(player, empty, $"{extension}: empty file");
            player.Load(AudioFixture(extension));
            player.Play();
            Check(player.IsOutputPlaying && player.Volume == .37f, $"{extension}: valid playback after bad loads retains gain");
        }

        await CheckAudioRecoveryAsync(temporary.Path);
        await CheckAudioQueueAsync(temporary.Path);
        CheckAudioDeviceLayout();
    }

    private static void ExpectAudioLoadFailure(NAudioPlayer player, string path, string name)
    {
        var failed = false;
        try { player.Load(path); }
        catch { failed = true; }
        Check(failed && player.Duration == TimeSpan.Zero && !player.IsOutputPlaying, name);
    }

    private static async Task CheckAudioRecoveryAsync(string directory)
    {
        var backend = new TestAudioBackend();
        using var player = new NAudioPlayer(backend);
        var ended = 0;
        player.PlaybackEnded += (_, _) => ended++;
        player.Load(AudioFixture("wav"));
        player.Play();
        backend.Last!.ReadAhead(TimeSpan.FromSeconds(1));
        backend.Last.RenderedPosition = TimeSpan.FromSeconds(.4);
        _ = player.PresentationPosition;
        player.SelectOutputDevice("B");
        Check(backend.LastId == "B" && player.IsOutputPlaying && Math.Abs(player.Position.TotalSeconds - .4) < .01,
            "Device switch resumes from audible position rather than decoded read-ahead");
        backend.Devices.RemoveAll(d => d.Id == "B");
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(backend.LastId == "A" && player.SelectedOutputDeviceId == "B" && player.OutputMessage is not null && player.IsOutputPlaying,
            "Unplug uses default device and retains selected endpoint preference");
        backend.Devices.Add(new("B", "Headphones"));
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(backend.LastId == "B" && player.OutputMessage is null && ended == 0, "Reconnected selected endpoint is restored without track transition");

        player.SelectOutputDevice(null);
        backend.DefaultId = "B";
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(backend.LastId == "B" && player.IsOutputPlaying, "Windows default selection follows a changed default endpoint");
        var outputCount = backend.Outputs.Count;
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(backend.Outputs.Count == outputCount, "Unrelated endpoint notifications do not restart audio");

        player.Pause();
        backend.Suspend();
        backend.Resume();
        await Task.Delay(650);
        Check(!player.IsOutputPlaying && ended == 0, "Paused sleep/resume stays paused");
        player.Play();
        backend.Last!.RenderedPosition = TimeSpan.FromSeconds(.2);
        var beforeSleep = player.PresentationPosition;
        backend.Suspend();
        Check(!player.IsOutputPlaying, "Sleep closes the active output");
        backend.Resume();
        await Task.Delay(650);
        Check(player.IsOutputPlaying && Math.Abs((player.Position - beforeSleep).TotalSeconds) < .01 && ended == 0,
            "Playing sleep/resume restores the audible position");

        backend.Last!.Fail(new IOException("Device invalidated"));
        Check(!player.IsOutputPlaying && player.OutputMessage!.Contains("Device invalidated") && ended == 0,
            "Asynchronous device error is surfaced and never signals EOF");
        player.Play();
        Check(player.IsOutputPlaying && player.OutputMessage is null, "Play retries after output failure");
        backend.Last!.Drain();
        backend.Last.Fail(new InvalidDataException("Decoder failure at EOF"));
        Check(ended == 0 && player.OutputMessage!.Contains("Decoder failure"), "Decode error after read-ahead EOF cannot advance the queue");

        backend.FailInit = true;
        player.Play();
        Check(!player.IsOutputPlaying && player.OutputMessage!.Contains("init failure") && backend.Last!.Disposed,
            "Output init failure is visible and releases the partial output");
        backend.FailInit = false;
        backend.FailPlay = true;
        player.Play();
        Check(!player.IsOutputPlaying && player.OutputMessage!.Contains("play failure"), "Synchronous play failure is visible");
        backend.FailPlay = false;
        backend.Devices.Clear();
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(!player.IsOutputPlaying && player.OutputMessage is not null && player.Duration.TotalSeconds > 0,
            "No output devices preserves loaded track for recovery");
        player.Pause();
        backend.Devices.Add(new("B", "Headphones"));
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(!player.IsOutputPlaying && player.OutputMessage is null, "Pause while disconnected cancels automatic resume");
        player.Play();
        Check(player.IsOutputPlaying && ended == 0, "Playback recovers when an endpoint returns");
        backend.Last!.FailPosition = true;
        _ = player.PresentationPosition;
        Check(!player.IsOutputPlaying && player.OutputMessage!.Contains("clock failure"), "Device-clock failure cannot crash visual updates");

        player.Play();
        backend.Suspend();
        backend.FailInit = true;
        backend.Resume();
        await Task.Delay(650);
        Check(!player.IsOutputPlaying && player.OutputMessage is not null, "Resume surfaces an endpoint that is not ready yet");
        backend.FailInit = false;
        await Task.Delay(1100);
        Check(player.IsOutputPlaying && player.OutputMessage is null, "Resume retries delayed endpoint initialization without another device notification");

        var store = new JsonUiPreferencesStore(Path.Combine(directory, "audio-preferences.json"));
        store.SaveVolume(35, 35);
        store.SaveOutputDeviceId("B");
        Check(store.LoadOutputDeviceId() == "B" && store.LoadVolume().Volume == 35, "Stable endpoint ID persists without overwriting volume");
        store.SaveOutputDeviceId(null);
        Check(store.LoadOutputDeviceId() is null, "Windows default preference persists");

        player.Dispose();
        backend.NotifyDevicesChanged();
        backend.Suspend();
        backend.Resume();
        await Task.Delay(650);
        Check(backend.Disposed && backend.Outputs.All(o => o.Disposed), "Disposal detaches notifications and closes all outputs");
    }

    private static async Task CheckAudioQueueAsync(string directory)
    {
        var backend = new TestAudioBackend();
        using var player = new NAudioPlayer(backend);
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player, monitorLibrary: false);
        foreach (var extension in AudioFormats)
            vm.Queue.Add(new() { FilePath = AudioFixture(extension), Title = extension, ArtworkData = [] });
        vm.PlayCommand.Execute(null);
        foreach (var extension in AudioFormats)
        {
            Check(vm.IsPlaying && vm.CurrentTrack?.Title == extension, $"Queue transitions into {extension}");
            backend.Last!.Drain();
            backend.Last.End();
        }
        Check(!vm.IsPlaying && vm.IsQueueEmpty, "Mixed-format queue exhausts normally");
        vm.PlayCommand.Execute(null);
        Check(vm.IsPlaying && player.Position == TimeSpan.Zero, "Replay after natural EOF starts at zero");

        vm.Queue.Add(new() { FilePath = AudioFixture("wav"), Title = "next", ArtworkData = [] });
        backend.Last!.Fail(new IOException("Disconnected"));
        Check(!vm.IsPlaying && vm.AudioOutputMessage!.Contains("Disconnected") && vm.Queue.Count == 1 && vm.CurrentTrack is not null,
            "Device errors update UI and preserve current track and queue");
        backend.FailInit = true;
        vm.PlayCommand.Execute(null);
        Check(!vm.IsPlaying && vm.CurrentTrack is not null && vm.Queue.Count == 1, "Unavailable device does not consume queued tracks");
        backend.FailInit = false;
        backend.NotifyDevicesChanged();
        await Task.Delay(650);
        Check(vm.IsPlaying && vm.AudioOutputMessage is null, "Automatic recovery updates UI play state");

        foreach (var extension in AudioFormats)
        {
            var corrupt = Path.Combine(directory, "queue-corrupt." + extension);
            File.WriteAllText(corrupt, "bad data");
            vm.Queue.Clear();
            vm.Queue.Add(new() { FilePath = corrupt, Title = "bad", ArtworkData = [] });
            vm.Queue.Add(new() { FilePath = Path.Combine(directory, "missing." + extension), Title = "missing" });
            vm.Queue.Add(new() { FilePath = AudioFixture(extension), Title = extension, ArtworkData = [] });
            vm.NextCommand.Execute(null);
            Check(vm.CurrentTrack?.Title == extension && vm.IsPlaying && vm.IsQueueEmpty &&
                  vm.PlaybackError!.Contains("bad") && vm.PlaybackError.Contains("missing"), $"{extension}: queue skips and reports corrupt and missing files");
        }
        vm.RepeatMode = PlaybackRepeatMode.One;
        backend.Last!.Drain();
        backend.Last.End();
        Check(vm.IsPlaying && player.Position == TimeSpan.Zero, "Real-reader repeat-one reloads current track");
        vm.RepeatMode = PlaybackRepeatMode.All;
        vm.Queue.Add(new() { FilePath = AudioFixture("wav"), Title = "repeat-next", ArtworkData = [] });
        backend.Last!.Drain();
        backend.Last.End();
        Check(vm.CurrentTrack?.Title == "repeat-next" && vm.Queue.Count == 1, "Real-reader repeat-all recycles exactly once");
    }

    private static void CheckAudioDeviceLayout()
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new NAudioPlayer(new TestAudioBackend()), monitorLibrary: false);
        var window = new MainWindow(vm);
        window.Show();
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("VolumePopup");
        popup.IsOpen = true;
        window.UpdateLayout();
        var selector = (System.Windows.Controls.ComboBox)window.FindName("AudioOutputSelector");
        Check(selector.Items.Count == 3 && selector.SelectedItem is AudioOutputDevice { Id: null } && selector.ActualWidth > 100,
            "Output selector binds default and named endpoints in volume popup");
        var content = (System.Windows.FrameworkElement)popup.Child;
        content.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth,
            (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        Directory.CreateDirectory("artifacts");
        using (var stream = File.Create("artifacts/audio-output.png")) encoder.Save(stream);
        selector.SelectedIndex = 2;
        Check(vm.SelectedAudioOutputDevice?.Id == "B", "Output selection updates the player binding");
        popup.IsOpen = false;
        window.Close();
    }

    private static async Task CheckAudioDeviceLiveAsync()
    {
        using var player = new NAudioPlayer();
        Check(player.OutputDevices.Count > 1, "Windows Core Audio enumerates native endpoints");
        using var backend = new WindowsAudioBackend();
        var defaultDeviceId = backend.GetDefaultDeviceId();
        foreach (var extension in AudioFormats)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (_, _) => ended.TrySetResult();
            player.PlaybackEnded += handler;
            player.Load(AudioFixture(extension));
            player.Play();
            player.SelectOutputDevice(defaultDeviceId);
            Check(player.IsOutputPlaying, $"Native WASAPI starts {extension}: {player.OutputMessage}");
            await Task.Delay(200);
            Check(player.PresentationPosition > TimeSpan.Zero, $"Native {extension}: output clock advances");
            player.Pause();
            player.Seek(TimeSpan.FromSeconds(.5));
            Check(!player.IsOutputPlaying, $"Native {extension}: paused seeking stays paused");
            player.Play();
            player.Seek(TimeSpan.FromSeconds(1.5));
            try { await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"Native {extension} did not end: position={player.Position}, duration={player.Duration}, playing={player.IsOutputPlaying}, error={player.OutputMessage}");
            }
            Check(player.PresentationPosition == player.Duration && player.OutputMessage is null,
                $"Native {extension}: playing seek drains to natural EOF");
            player.PlaybackEnded -= handler;
        }
    }

    private sealed class TestAudioBackend : IAudioBackend
    {
        public event Action? DevicesChanged;
        public event Action<bool>? PowerChanged;
        public List<AudioOutputDevice> Devices { get; } = [new("A", "Speakers"), new("B", "Headphones")];
        public string DefaultId { get; set; } = "A";
        public bool FailInit { get; set; }
        public bool FailPlay { get; set; }
        public bool Disposed { get; private set; }
        public List<TestAudioOutput> Outputs { get; } = [];
        public TestAudioOutput? Last => Outputs.LastOrDefault();
        public string? LastId { get; private set; }
        public IReadOnlyList<AudioOutputDevice> GetDevices() => Devices.ToArray();
        public string GetDefaultDeviceId() => Devices.Any(d => d.Id == DefaultId) ? DefaultId : throw new IOException("No audio device");
        public IAudioOutput CreateOutput(string id)
        {
            LastId = id;
            var output = new TestAudioOutput { FailInit = FailInit, FailPlay = FailPlay };
            Outputs.Add(output);
            return output;
        }
        public void NotifyDevicesChanged() => DevicesChanged?.Invoke();
        public void Suspend() => PowerChanged?.Invoke(true);
        public void Resume() => PowerChanged?.Invoke(false);
        public void Dispose() => Disposed = true;
    }

    private sealed class TestAudioOutput : IAudioOutput
    {
        private IWaveProvider? _source;
        private TimeSpan _rendered;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public TimeSpan RenderedPosition
        {
            get => FailPosition ? throw new IOException("clock failure") : _rendered;
            set => _rendered = value;
        }
        public bool FailInit { get; init; }
        public bool FailPlay { get; init; }
        public bool FailPosition { get; set; }
        public bool Disposed { get; private set; }
        public void Init(IWaveProvider source)
        {
            if (FailInit) throw new IOException("init failure");
            _source = source;
        }
        public void Play()
        {
            if (FailPlay) throw new IOException("play failure");
            PlaybackState = PlaybackState.Playing;
        }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void ReadAhead(TimeSpan time)
        {
            var bytes = (int)(time.TotalSeconds * _source!.WaveFormat.AverageBytesPerSecond);
            var buffer = new byte[bytes - bytes % _source.WaveFormat.BlockAlign];
            _source.Read(buffer.AsSpan());
        }
        public void Drain()
        {
            var buffer = new byte[4096];
            while (_source!.Read(buffer.AsSpan()) > 0) { }
        }
        public EventHandler<StoppedEventArgs>? CaptureStoppedCallback() => PlaybackStopped;
        public void End() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Fail(Exception ex) { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(ex)); }
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}
