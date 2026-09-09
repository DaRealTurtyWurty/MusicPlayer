using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckPlaybackModePersistence()
    {
        using var directory = new TemporaryTestDirectory("MusicPlayerPlaybackModes");
        var preferencesPath = Path.Combine(directory.Path, "preferences.json");
        var sessionPath = Path.Combine(directory.Path, "music.db");
        var preferences = new JsonUiPreferencesStore(preferencesPath);
        var sessionStore = new SqliteMusicStore(sessionPath);
        sessionStore.SaveSession(new PlaybackSession(Track("Current"), TimeSpan.FromSeconds(17),
            new[] { Track("A"), Track("B"), Track("A"), Track("C") }));
        MainViewModel Create(FakePlayer player) => new(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            random: new Random(42), uiPreferencesStore: new JsonUiPreferencesStore(preferencesPath),
            playbackSessionStore: new SqliteMusicStore(sessionPath), monitorLibrary: false);

        Check(preferences.LoadPlaybackModes() == (false, PlaybackRepeatMode.Off),
            "Missing preferences default shuffle and repeat to off");
        foreach (var mode in new[] { PlaybackRepeatMode.All, PlaybackRepeatMode.One, PlaybackRepeatMode.Off })
        {
            string[] savedOrder;
            bool shuffle;
            using (var vm = Create(new FakePlayer()))
            {
                vm.ToggleShuffleCommand.Execute(null);
                vm.CycleRepeatCommand.Execute(null);
                shuffle = vm.IsShuffleEnabled;
                Check(vm.RepeatMode == mode && preferences.LoadPlaybackModes() == (shuffle, mode),
                    $"Shuffle and repeat {mode} are saved immediately when controls change");
                vm.Volume = 38;
                vm.IsQueueOpen = true;
                vm.SelectedPage = AppPage.Artists;
                Check(preferences.LoadPlaybackModes() == (shuffle, mode),
                    "Saving volume, queue visibility and navigation preserves playback modes");
                savedOrder = vm.Queue.Select(t => t.FilePath).ToArray();
            }
            var player = new FakePlayer();
            using var restored = Create(player);
            Check(restored.IsShuffleEnabled == shuffle && restored.RepeatMode == mode &&
                  restored.IsRepeatOne == (mode == PlaybackRepeatMode.One) &&
                  restored.IsRepeatEnabled == (mode != PlaybackRepeatMode.Off),
                $"Restart restores shuffle and repeat {mode}, including UI state");
            Check(restored.Queue.Select(t => t.FilePath).SequenceEqual(savedOrder) &&
                  restored.CurrentTrack?.Title == "Current" && restored.PositionSeconds == 17 &&
                  player.PlayCount == 0 && !restored.IsPlaying,
                "Restoring playback modes preserves queue order, duplicate entries and paused position");
            Check(restored.Volume == 38 && restored.IsQueueOpen && restored.SelectedPage == AppPage.Artists,
                "Saving playback modes preserves other preferences");
        }

        File.WriteAllText(preferencesPath, "{\"Volume\":25,\"IsQueueOpen\":true}");
        using (var legacy = Create(new FakePlayer()))
            Check(!legacy.IsShuffleEnabled && legacy.RepeatMode == PlaybackRepeatMode.Off && legacy.Volume == 25 && legacy.IsQueueOpen,
                "Older preference files retain their settings and default new playback modes to off");
        File.WriteAllText(preferencesPath, "{\"IsShuffleEnabled\":true,\"RepeatMode\":999}");
        using (var invalid = Create(new FakePlayer()))
            Check(invalid.IsShuffleEnabled && invalid.RepeatMode == PlaybackRepeatMode.Off,
                "Invalid stored repeat modes fall back to off without losing shuffle");
        File.WriteAllText(preferencesPath, "invalid JSON");
        using (var corrupt = Create(new FakePlayer()))
            Check(!corrupt.IsShuffleEnabled && corrupt.RepeatMode == PlaybackRepeatMode.Off,
                "Corrupt preference files use safe playback mode defaults");

        var blockedPath = Path.Combine(directory.Path, "blocked");
        Directory.CreateDirectory(blockedPath);
        using var unavailable = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            uiPreferencesStore: new JsonUiPreferencesStore(blockedPath), monitorLibrary: false);
        unavailable.ToggleShuffleCommand.Execute(null);
        unavailable.CycleRepeatCommand.Execute(null);
        Check(unavailable.IsShuffleEnabled && unavailable.RepeatMode == PlaybackRepeatMode.All,
            "Preference write failures do not prevent changing playback modes");
    }
}
