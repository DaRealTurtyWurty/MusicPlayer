using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class JsonUiPreferencesStore(string? path = null) : IUiPreferencesStore
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "preferences.json");

    public bool LoadQueueOpen() => Load().IsQueueOpen;

    public AppPage LoadSelectedPage()
    {
        var page = Load().SelectedPage;
        return Enum.IsDefined(page) ? page : AppPage.Library;
    }

    public void SaveQueueOpen(bool isOpen) => Save(Load() with { IsQueueOpen = isOpen });

    public void SaveSelectedPage(AppPage page) => Save(Load() with
    {
        SelectedPage = Enum.IsDefined(page) ? page : AppPage.Library
    });

    public (double Volume, double VolumeBeforeMute) LoadVolume()
    {
        var preferences = Load();
        return NormalizeVolume(preferences.Volume, preferences.VolumeBeforeMute);
    }

    public void SaveVolume(double volume, double volumeBeforeMute)
    {
        var normalized = NormalizeVolume(volume, volumeBeforeMute);
        Save(Load() with { Volume = normalized.Volume, VolumeBeforeMute = normalized.VolumeBeforeMute });
    }

    public (bool ShuffleEnabled, PlaybackRepeatMode RepeatMode) LoadPlaybackModes()
    {
        var preferences = Load();
        return (preferences.IsShuffleEnabled, Enum.IsDefined(preferences.RepeatMode)
            ? preferences.RepeatMode : PlaybackRepeatMode.Off);
    }

    public void SavePlaybackModes(bool shuffleEnabled, PlaybackRepeatMode repeatMode) => Save(Load() with
    {
        IsShuffleEnabled = shuffleEnabled,
        RepeatMode = Enum.IsDefined(repeatMode) ? repeatMode : PlaybackRepeatMode.Off
    });

    private static (double Volume, double VolumeBeforeMute) NormalizeVolume(double volume, double volumeBeforeMute)
    {
        volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 100) : 100;
        volumeBeforeMute = double.IsFinite(volumeBeforeMute) && volumeBeforeMute > 0
            ? Math.Clamp(volumeBeforeMute, 0, 100) : 100;
        return (volume, volume > 0 ? volume : volumeBeforeMute);
    }

    private Preferences Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path)) ?? new Preferences()
                : new Preferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Could not load UI preferences: {ex.Message}");
            return new Preferences();
        }
    }

    private void Save(Preferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preferences write failure must not prevent playback or interaction.
            Trace.TraceWarning($"Could not save UI preferences: {ex.Message}");
        }
    }

    private sealed record Preferences(bool IsQueueOpen = false, AppPage SelectedPage = AppPage.Library,
        double Volume = 100, double VolumeBeforeMute = 100,
        bool IsShuffleEnabled = false, PlaybackRepeatMode RepeatMode = PlaybackRepeatMode.Off);
}
