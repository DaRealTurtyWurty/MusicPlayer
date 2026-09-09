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

    public event EventHandler? PlaybackEnded;

    public TimeSpan Position =>
        _audioFile?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan Duration =>
        _audioFile?.TotalTime ?? TimeSpan.Zero;

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
            _outputDevice = new WaveOut();
            _outputDevice.Init(_volumeProvider);
            _outputDevice.PlaybackStopped += OnPlaybackStopped;
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
        _outputDevice?.Stop();

        _audioFile?.Position = 0;
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        if (position > _audioFile.TotalTime)
            position = _audioFile.TotalTime;

        _audioFile.CurrentTime = position;
    }

    private void DisposePlayback()
    {
        _playRequested = false;
        if (_outputDevice is not null)
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
        _outputDevice?.Dispose();
        _audioFile?.Dispose();

        _outputDevice = null;
        _audioFile = null;
        _volumeProvider = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore explicit stops, disposed outputs, and device errors.
        if (sender != _outputDevice || !_playRequested || e.Exception is not null ||
            _audioFile is null || _audioFile.Position < _audioFile.Length)
            return;

        _playRequested = false;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        DisposePlayback();
    }
}
