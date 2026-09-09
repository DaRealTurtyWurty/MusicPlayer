using System.IO;
using NAudio.SoundFile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicPlayer.Services;

public sealed class NAudioPlayer : IAudioPlayer, IDisposable
{
    private WaveOut? _outputDevice;
    private WaveStream? _audioFile;
    private VolumeSampleProvider? _volumeProvider;
    private float _volume = 1f;
    private bool _playRequested;
    private TimeSpan _outputOrigin;
    private bool _presentationEnded;

    public event EventHandler? PlaybackEnded;

    public TimeSpan Position =>
        _audioFile?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan Duration =>
        _audioFile?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan PresentationPosition
    {
        get
        {
            if (_presentationEnded) return Duration;
            if (_outputDevice is null || _outputDevice.PlaybackState == PlaybackState.Stopped) return _outputOrigin;
            var renderedSeconds = _outputDevice.GetPosition() / (double)_outputDevice.OutputWaveFormat.AverageBytesPerSecond;
            return TimeSpan.FromSeconds(Math.Clamp(_outputOrigin.TotalSeconds + renderedSeconds, 0, Duration.TotalSeconds));
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            if (!float.IsFinite(value)) return;
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider is not null)
                _volumeProvider.Volume = _volume;
        }
    }

    public void Load(string filePath)
    {
        DisposePlayback();

        try
        {
            _audioFile = CreateReader(filePath);
            _volumeProvider = new VolumeSampleProvider(_audioFile.ToSampleProvider()) { Volume = _volume };
            InitializeOutput();
        }
        catch
        {
            DisposePlayback();
            throw;
        }
    }


    private static WaveStream CreateReader(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".flac" => new SoundFileReader(filePath),
            ".ogg" => new SoundFileReader(filePath),
            ".opus" => new SoundFileReader(filePath),

            _ => new AudioFileReader(filePath)
        };
    }

    public void Play()
    {
        _playRequested = true;
        _outputDevice?.Play();
    }

    public void Pause()
    {
        _outputDevice?.Pause();
    }

    public void Stop()
    {
        _playRequested = false;
        DisposeOutput();

        _audioFile?.Position = 0;
        _outputOrigin = TimeSpan.Zero;
        _presentationEnded = false;
        if (_volumeProvider is not null) InitializeOutput();
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        if (position > _audioFile.TotalTime)
            position = _audioFile.TotalTime;

        if (_outputDevice?.PlaybackState == PlaybackState.Stopped && _audioFile.CurrentTime == position && !_presentationEnded)
            return;

        var resume = _outputDevice?.PlaybackState == PlaybackState.Playing;
        // A new output discards pre-seek buffers and gives the rendered byte counter a
        // fresh origin. Detach first so the old output cannot end the current track.
        DisposeOutput();
        _audioFile.CurrentTime = position;
        _outputOrigin = _audioFile.CurrentTime;
        _presentationEnded = false;
        InitializeOutput();
        if (resume) Play();
    }

    private void InitializeOutput()
    {
        _outputDevice = new WaveOut();
        _outputDevice.Init(_volumeProvider!);
        _outputDevice.PlaybackStopped += OnPlaybackStopped;
    }

    private void DisposeOutput()
    {
        if (_outputDevice is not null) _outputDevice.PlaybackStopped -= OnPlaybackStopped;
        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    private void DisposePlayback()
    {
        _playRequested = false;
        DisposeOutput();
        _audioFile?.Dispose();

        _outputDevice = null;
        _audioFile = null;
        _volumeProvider = null;
        _outputOrigin = TimeSpan.Zero;
        _presentationEnded = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore explicit stops, disposed outputs, and device errors.
        if (sender != _outputDevice || !_playRequested || e.Exception is not null ||
            _audioFile is null || _audioFile.Position < _audioFile.Length)
            return;

        _playRequested = false;
        _presentationEnded = true;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        DisposePlayback();
    }
}
