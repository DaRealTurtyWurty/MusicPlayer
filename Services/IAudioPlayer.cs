namespace MusicPlayer.Services;

public interface IAudioPlayer
{
    event EventHandler? PlaybackEnded;

    TimeSpan Position { get; }

    /// <summary>Output presentation position for synchronized visuals; excludes decoded audio still queued.</summary>
    TimeSpan PresentationPosition => Position;

    TimeSpan Duration { get; }

    /// <summary>Playback gain from 0 (silent) to 1 (full volume).</summary>
    float Volume { get; set; }

    void Load(string filePath);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);
}
