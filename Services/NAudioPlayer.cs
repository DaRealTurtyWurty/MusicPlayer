using System.IO;
using System.Windows.Threading;
using NAudio.SoundFile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicPlayer.Services;

public sealed class NAudioPlayer : IAudioPlayer, IAudioDevicePlayer, IDisposable
{
    private readonly IAudioBackend _backend;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private IAudioOutput? _outputDevice;
    private string? _activeDeviceId;
    private WaveStream? _audioFile;
    private VolumeSampleProvider? _volumeProvider;
    private EndOfStreamWaveProvider? _outputSource;
    private float _volume = 1f;
    private bool _playRequested;
    private TimeSpan _outputOrigin;
    private TimeSpan _lastPresentationPosition;
    private bool _presentationEnded;
    private bool _suspended;
    private int _resumeRetries;
    private bool _disposed;

    public NAudioPlayer() : this(new WindowsAudioBackend()) { }
    internal NAudioPlayer(IAudioBackend backend)
    {
        _backend = backend;
        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _deviceRefreshTimer.Tick += OnDeviceRefreshTick;
        _backend.DevicesChanged += OnDevicesChanged;
        _backend.PowerChanged += OnPowerChanged;
        RefreshDevices();
    }

    public event EventHandler? PlaybackEnded;
    public event EventHandler? OutputDevicesChanged;
    public event EventHandler<AudioOutputStatusEventArgs>? OutputStatusChanged;
    public IReadOnlyList<AudioOutputDevice> OutputDevices { get; private set; } = [];
    public string? SelectedOutputDeviceId { get; private set; }
    public bool IsOutputPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
    public string? OutputMessage { get; private set; }
    public TimeSpan Position => _audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _audioFile?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan PresentationPosition
    {
        get
        {
            if (_presentationEnded) return Duration;
            if (_outputDevice is null || _outputDevice.PlaybackState == PlaybackState.Stopped) return _outputOrigin;
            try
            {
                _lastPresentationPosition = Clamp(_outputOrigin + _outputDevice.RenderedPosition);
            }
            catch (Exception ex) { HandleOutputFailure(ex); }
            return _lastPresentationPosition;
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            if (!float.IsFinite(value)) return;
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider is not null) _volumeProvider.Volume = _volume;
        }
    }

    public void Load(string filePath)
    {
        DisposePlayback();
        try
        {
            _audioFile = CreateReader(filePath);
            if (_audioFile.Length == 0 || _audioFile.TotalTime <= TimeSpan.Zero)
                throw new InvalidDataException("The audio file contains no playable samples.");
            _volumeProvider = new VolumeSampleProvider(_audioFile.ToSampleProvider()) { Volume = _volume };
        }
        catch { DisposePlayback(); throw; }
        TryInitializeOutput();
    }

    internal static WaveStream CreateReader(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The audio file is missing. Locate it or remove it from the queue.", filePath);
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".flac" or ".ogg" or ".opus" => new SoundFileReader(filePath),
            _ => new AudioFileReader(filePath)
        };
    }

    public void Play()
    {
        if (_audioFile is null) return;
        _playRequested = true;
        if (_suspended) return;
        if (_outputDevice is null && !TryInitializeOutput()) return;
        try { _outputDevice!.Play(); PublishStatus(OutputMessage); }
        catch (Exception ex) { HandleOutputFailure(ex); }
    }

    public void Pause()
    {
        _playRequested = false;
        try { _outputDevice?.Pause(); PublishStatus(OutputMessage); }
        catch (Exception ex) { HandleOutputFailure(ex); }
    }

    public void Stop()
    {
        _playRequested = false;
        DisposeOutput();
        if (_audioFile is not null) _audioFile.Position = 0;
        _outputOrigin = _lastPresentationPosition = TimeSpan.Zero;
        _presentationEnded = false;
        PublishStatus(null);
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile is null) return;
        position = Clamp(position);
        DisposeOutput();
        _audioFile.CurrentTime = position;
        _outputOrigin = _lastPresentationPosition = _audioFile.CurrentTime;
        _presentationEnded = false;
        if (TryInitializeOutput() && _playRequested) Play();
    }

    public void SelectOutputDevice(string? deviceId)
    {
        SelectedOutputDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        RefreshDevices();
        RecoverOutput(force: true);
    }

    private TimeSpan Clamp(TimeSpan position) => TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));

    private bool TryInitializeOutput()
    {
        if (_volumeProvider is null || _suspended) return false;
        try
        {
            var (id, fallback) = ResolveDevice();
            var output = _backend.CreateOutput(id);
            var source = new EndOfStreamWaveProvider(_volumeProvider.ToWaveProvider());
            try { output.Init(source); }
            catch { output.Dispose(); throw; }
            _outputDevice = output;
            _outputSource = source;
            _activeDeviceId = id;
            output.PlaybackStopped += OnPlaybackStopped;
            PublishStatus(fallback ? "Selected output device is unavailable. Using Windows default until it reconnects." : null);
            return true;
        }
        catch (Exception ex) { HandleOutputFailure(ex); return false; }
    }

    private (string Id, bool Fallback) ResolveDevice()
    {
        if (SelectedOutputDeviceId is { } selected && OutputDevices.Any(d => d.Id == selected)) return (selected, false);
        return (_backend.GetDefaultDeviceId(), SelectedOutputDeviceId is not null);
    }

    private void RecoverOutput(bool force)
    {
        if (_audioFile is null || _suspended) return;
        if (!force && _outputDevice is not null)
        {
            try
            {
                if (ResolveDevice().Id == _activeDeviceId) return;
            }
            catch { /* Reopening below reports the unavailable device. */ }
        }
        var position = PresentationPosition;
        DisposeOutput();
        try
        {
            _audioFile.CurrentTime = Clamp(position);
            _outputOrigin = _lastPresentationPosition = _audioFile.CurrentTime;
            if (TryInitializeOutput() && _playRequested) Play();
        }
        catch (Exception ex) { HandleOutputFailure(ex); }
    }

    private void HandleOutputFailure(Exception exception)
    {
        // Keep the reader and the user's playback intent for retry/reconnection. Never
        // signal EOF for an output/decoder exception, even if read-ahead reached EOF.
        DisposeOutput();
        _outputOrigin = _lastPresentationPosition;
        if (_audioFile is not null)
        {
            try { _audioFile.CurrentTime = Clamp(_outputOrigin); }
            catch { /* Preserve the original failure. */ }
        }
        PublishStatus($"Audio playback stopped: {exception.Message} Select an output device or press Play to retry.");
    }

    private void PublishStatus(string? message)
    {
        OutputMessage = message;
        OutputStatusChanged?.Invoke(this, new(IsOutputPlaying, message));
    }

    private void RefreshDevices()
    {
        try
        {
            OutputDevices = new[] { new AudioOutputDevice(null, "Windows default") }.Concat(_backend.GetDevices()).ToArray();
            OutputDevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OutputDevices = [new(null, "Windows default")];
            OutputDevicesChanged?.Invoke(this, EventArgs.Empty);
            PublishStatus($"Could not list audio output devices: {ex.Message}");
        }
    }

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }

    private void OnDevicesChanged() => Dispatch(() =>
    {
        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Start();
    });

    private void OnDeviceRefreshTick(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        RefreshDevices();
        RecoverOutput(force: false);
        if (!_suspended && _audioFile is not null && _outputDevice is null && _resumeRetries > 0)
        {
            _resumeRetries--;
            _deviceRefreshTimer.Interval = TimeSpan.FromSeconds(1);
            _deviceRefreshTimer.Start();
        }
        else
        {
            _resumeRetries = 0;
            _deviceRefreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        }
    }

    private void OnPowerChanged(bool suspended) => Dispatch(() =>
    {
        if (suspended)
        {
            var position = PresentationPosition;
            _suspended = true;
            _resumeRetries = 0;
            _deviceRefreshTimer.Stop();
            DisposeOutput();
            _outputOrigin = _lastPresentationPosition = position;
            try { if (_audioFile is not null) _audioFile.CurrentTime = Clamp(position); }
            catch (Exception ex) { HandleOutputFailure(ex); return; }
            PublishStatus("Audio playback suspended while Windows sleeps.");
        }
        else
        {
            _suspended = false;
            _resumeRetries = 3;
            if (_audioFile is null) PublishStatus(null);
            // Endpoint availability can lag behind the power notification.
            _deviceRefreshTimer.Start();
        }
    });

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => Dispatch(() =>
    {
        if (sender != _outputDevice || _audioFile is null) return;
        if (e.Exception is not null) { HandleOutputFailure(e.Exception); return; }
        if (!_playRequested) return;
        if (_outputSource?.EndReached != true)
        {
            HandleOutputFailure(new IOException("The output device stopped before the track ended."));
            return;
        }
        _playRequested = false;
        _presentationEnded = true;
        PublishStatus(null);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    });

    private void DisposeOutput()
    {
        var output = _outputDevice;
        _outputDevice = null;
        _outputSource = null;
        _activeDeviceId = null;
        if (output is null) return;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Could not close audio output: {ex.Message}"); }
    }

    private void DisposePlayback()
    {
        _resumeRetries = 0;
        _playRequested = false;
        DisposeOutput();
        _audioFile?.Dispose();
        _audioFile = null;
        _volumeProvider = null;
        _outputOrigin = _lastPresentationPosition = TimeSpan.Zero;
        _presentationEnded = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tick -= OnDeviceRefreshTick;
        _backend.DevicesChanged -= OnDevicesChanged;
        _backend.PowerChanged -= OnPowerChanged;
        DisposePlayback();
        _backend.Dispose();
    }

    // Compressed readers can estimate Length beyond the actual decoded samples
    // (e.g. MP3 padding). EOF is determined by the decoder, not that estimate.
    private sealed class EndOfStreamWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private volatile bool _endReached;
        public bool EndReached => _endReached;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            var read = source.Read(buffer);
            if (buffer.Length > 0 && read == 0) _endReached = true;
            return read;
        }
    }
}
