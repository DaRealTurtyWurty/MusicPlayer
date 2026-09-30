using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private bool _refreshingOutputDevices;
    public ObservableCollection<AudioOutputDevice> AudioOutputDevices { get; } = [];
    public bool CanSelectAudioOutput => _audioPlayer is IAudioDevicePlayer;
    [ObservableProperty] private AudioOutputDevice? selectedAudioOutputDevice;
    [ObservableProperty] private string? audioOutputMessage;

    private void InitializeAudioDevices()
    {
        if (_audioPlayer is not IAudioDevicePlayer player) return;
        player.OutputDevicesChanged += OnOutputDevicesChanged;
        player.OutputStatusChanged += OnOutputStatusChanged;
        player.SelectOutputDevice(_uiPreferencesStore?.LoadOutputDeviceId());
        RefreshOutputDevices();
        AudioOutputMessage = player.OutputMessage;
    }

    private void OnOutputDevicesChanged(object? sender, EventArgs e) => RefreshOutputDevices();

    private void RefreshOutputDevices()
    {
        if (_audioPlayer is not IAudioDevicePlayer player) return;
        _refreshingOutputDevices = true;
        try
        {
            AudioOutputDevices.Clear();
            foreach (var device in player.OutputDevices) AudioOutputDevices.Add(device);
            var selected = AudioOutputDevices.FirstOrDefault(d => d.Id == player.SelectedOutputDeviceId);
            if (selected is null)
            {
                selected = new(player.SelectedOutputDeviceId, "Selected device (unavailable)");
                AudioOutputDevices.Add(selected);
            }
            SelectedAudioOutputDevice = selected;
        }
        finally { _refreshingOutputDevices = false; }
    }

    partial void OnSelectedAudioOutputDeviceChanged(AudioOutputDevice? value)
    {
        if (_refreshingOutputDevices || value is null || _audioPlayer is not IAudioDevicePlayer player) return;
        player.SelectOutputDevice(value.Id);
        _uiPreferencesStore?.SaveOutputDeviceId(value.Id);
    }

    private void OnOutputStatusChanged(object? sender, AudioOutputStatusEventArgs e)
    {
        AudioOutputMessage = e.Message;
        IsPlaying = e.IsPlaying;
        if (e.IsPlaying) IsPlaybackStopped = false;
    }

    private bool TryPlaybackAction(Action action) => _playback.TryPlaybackAction(action);

    private void DisposeAudioDevices()
    {
        if (_audioPlayer is not IAudioDevicePlayer player) return;
        player.OutputDevicesChanged -= OnOutputDevicesChanged;
        player.OutputStatusChanged -= OnOutputStatusChanged;
    }
}
