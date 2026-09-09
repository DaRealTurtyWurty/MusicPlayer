namespace MusicPlayer.Services;

public interface IAudioPlayer
{
    event EventHandler? PlaybackEnded;

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    /// <summary>Playback gain from 0 (silent) to 1 (full volume).</summary>
    float Volume { get; set; }

    void Load(string filePath);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);
}
