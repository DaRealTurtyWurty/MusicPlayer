namespace MusicPlayer.Services;

public sealed record AudioOutputDevice(string? Id, string Name);
public sealed class AudioOutputStatusEventArgs(bool isPlaying, string? message) : EventArgs
{
    public bool IsPlaying { get; } = isPlaying;
    public string? Message { get; } = message;
}

public interface IAudioDevicePlayer
{
    event EventHandler? OutputDevicesChanged;
    event EventHandler<AudioOutputStatusEventArgs>? OutputStatusChanged;
    IReadOnlyList<AudioOutputDevice> OutputDevices { get; }
    string? SelectedOutputDeviceId { get; }
    bool IsOutputPlaying { get; }
    string? OutputMessage { get; }
    void SelectOutputDevice(string? deviceId);
}
