using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MusicPlayer.Services;

internal interface IAudioOutput : IDisposable
{
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    PlaybackState PlaybackState { get; }
    TimeSpan RenderedPosition { get; }
    void Init(IWaveProvider source);
    void Play();
    void Pause();
}

internal interface IAudioBackend : IDisposable
{
    event Action? DevicesChanged;
    event Action<bool>? PowerChanged;
    IReadOnlyList<AudioOutputDevice> GetDevices();
    string GetDefaultDeviceId();
    IAudioOutput CreateOutput(string deviceId);
}

internal sealed class WindowsAudioBackend : IAudioBackend
{
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    public event Action? DevicesChanged;
    public event Action<bool>? PowerChanged;

    public WindowsAudioBackend() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    private MMDeviceEnumerator Enumerator
    {
        get
        {
            if (_enumerator is not null) return _enumerator;
            var enumerator = new MMDeviceEnumerator();
            try
            {
                _notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
                _notifications.DeviceStateChanged += (_, _) => DevicesChanged?.Invoke();
                _notifications.DeviceAdded += (_, _) => DevicesChanged?.Invoke();
                _notifications.DeviceRemoved += (_, _) => DevicesChanged?.Invoke();
                _notifications.PropertyValueChanged += (_, _) => DevicesChanged?.Invoke();
                _notifications.DefaultDeviceChanged += (_, e) =>
                {
                    if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) DevicesChanged?.Invoke();
                };
            }
            catch { enumerator.Dispose(); throw; }
            return _enumerator = enumerator;
        }
    }

    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        var devices = new List<AudioOutputDevice>();
        using var endpoints = Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in endpoints)
        {
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        }
        return devices;
    }

    public string GetDefaultDeviceId()
    {
        using var device = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    public IAudioOutput CreateOutput(string deviceId) => new WindowsAudioOutput(Enumerator.GetDevice(deviceId));

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) PowerChanged?.Invoke(true);
        else if (e.Mode == PowerModes.Resume) PowerChanged?.Invoke(false);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_enumerator is null) return;
        try { _notifications?.Dispose(); _notifications = null; }
        finally { _enumerator.Dispose(); _enumerator = null; }
    }

    private sealed class WindowsAudioOutput : IAudioOutput
    {
        private readonly MMDevice _device;
        private readonly WasapiPlayer _output;
        public WindowsAudioOutput(MMDevice device)
        {
            _device = device;
            try
            {
                _output = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode()
                    .WithEventSync().WithLatency(300).WithMmcssThreadPriority("Audio")
                    .WithCategory(AudioStreamCategory.Media).Build();
            }
            catch { device.Dispose(); throw; }
            _output.PlaybackStopped += OnStopped;
        }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        private void OnStopped(object? sender, StoppedEventArgs args) => PlaybackStopped?.Invoke(this, args);
        public PlaybackState PlaybackState => _output.PlaybackState;
        public TimeSpan RenderedPosition => TimeSpan.FromSeconds(
            _output.GetPosition() / (double)_output.OutputWaveFormat.AverageBytesPerSecond);
        public void Init(IWaveProvider source) => _output.Init(source);
        public void Play() => _output.Play();
        public void Pause() => _output.Pause();
        public void Dispose()
        {
            _output.PlaybackStopped -= OnStopped;
            try { _output.Dispose(); }
            finally { _device.Dispose(); }
        }
    }
}
