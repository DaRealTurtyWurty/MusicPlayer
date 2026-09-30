using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckCrossfadeAsync()
    {
        var source = new GaplessSampleProvider(new TestSamples(Enumerable.Repeat(1f, 8).ToArray(), sampleRate: 4),
            TimeSpan.FromSeconds(2), 1);
        source.SetNext(new TestSamples(new float[8], sampleRate: 4), TimeSpan.FromSeconds(2));
        var samples = new float[20];
        var read = source.Read(samples);
        Check(read == 12 && samples.Take(read).SequenceEqual(new float[] { 1, 1, 1, 1, .75f, .5f, .25f, 0, 0, 0, 0, 0 }),
            "Crossfade overlaps exactly one second with complementary linear fades");
        Check(source.Boundary == TimeSpan.FromSeconds(2) && source.NextPosition == TimeSpan.FromSeconds(1),
            "Transition records the incoming track's consumed fade time");

        source = new GaplessSampleProvider(new TestSamples(Enumerable.Repeat(.5f, 8).ToArray(), sampleRate: 4, channels: 2),
            TimeSpan.FromSeconds(1), 12);
        source.SetNext(new TestSamples(Enumerable.Repeat(.5f, 8).ToArray(), sampleRate: 4, channels: 2), TimeSpan.FromSeconds(1));
        read = source.Read(samples);
        Check(read == 12 && samples.Take(read).All(s => s == .5f) && source.NextPosition == TimeSpan.FromSeconds(.5),
            "Short stereo tracks shorten the fade and preserve channel balance without boosting peaks");

        source = new GaplessSampleProvider(new TestSamples(Enumerable.Repeat(1f, 8).ToArray(), sampleRate: 4), TimeSpan.FromSeconds(2), 1);
        source.SetNext(new TestSamples(new float[8], sampleRate: 4), TimeSpan.FromSeconds(2));
        source.Read(samples.AsSpan(0, 5));
        Check(!source.SetNext(null), "Queue edits during a partially decoded fade require an output flush");
        source.Read(samples.AsSpan(5));
        Check(samples.Take(8).SequenceEqual(new float[] { 1, 1, 1, 1, .75f, .5f, .25f, 0 }),
            "Fade weights remain continuous across output buffers");
        source = new GaplessSampleProvider(new TestSamples([1, 2, 3, 4], sampleRate: 4), TimeSpan.FromSeconds(1), 1);
        read = source.Read(samples);
        Check(read == 4 && samples.Take(read).SequenceEqual(new float[] { 1, 2, 3, 4 }), "Final track plays fully without fading to silence");

        using var temporary = new TemporaryTestDirectory("MusicPlayerCrossfadeTests");
        var path = Path.Combine(temporary.Path, "preferences.json");
        var preferences = new JsonUiPreferencesStore(path);
        Check(!preferences.LoadCrossfade().Enabled, "Crossfade is optional and disabled by default");
        File.WriteAllText(path, "{\"Volume\":35}");
        Check(!preferences.LoadCrossfade().Enabled, "Existing preferences keep crossfade disabled");
        Check(new CrossfadeOptions(true, double.NaN).Normalize().DurationSeconds == 5
            && new CrossfadeOptions(true, 99).Normalize().DurationSeconds == 12, "Invalid fade durations normalize safely");
        var backend = new TestAudioBackend();
        using var player = new NAudioPlayer(backend);
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
            uiPreferencesStore: preferences, monitorLibrary: false);
        vm.IsGaplessPlaybackEnabled = false;
        vm.CrossfadeDurationSeconds = 1;
        vm.IsCrossfadeEnabled = true;
        var first = new Track { FilePath = AudioFixture("wav"), Title = "first", ArtworkData = [] };
        var second = new Track { FilePath = AudioFixture("flac"), Title = "second", ArtworkData = [] };
        vm.Queue.Add(first);
        vm.Queue.Add(second);
        vm.PlayCommand.Execute(null);
        var output = backend.Last!;
        output.ReadAhead(TimeSpan.FromSeconds(2.1));
        Check(vm.CurrentTrack == first && vm.Queue.Count == 1, "Crossfade read-ahead does not consume the visible queue");
        Check(vm.ListeningHistory.Count == 1 && first.PlayCount == 1 && second.PlayCount == 0,
            "Buffered crossfade audio does not count the incoming song before its audible transition");
        output.RenderedPosition = TimeSpan.FromSeconds(2.05);
        await Task.Delay(40);
        Check(vm.CurrentTrack == second && vm.IsQueueEmpty && !output.Disposed && vm.IsPlaying
            && player.PresentationPosition.TotalSeconds is > 1 and < 1.1,
            "Crossfade overrides disabled gapless and advances the clock by the consumed overlap");
        Check(vm.ListeningHistory.Count == 2 && second.PlayCount == 1, "Audible crossfade transition records exactly one incoming play");
        vm.PauseCommand.Execute(null);
        player.Seek(TimeSpan.FromSeconds(.5));
        Check(!player.IsOutputPlaying && Math.Abs(player.PresentationPosition.TotalSeconds - .5) < .01,
            "Seeking after crossfade stays on the incoming song and respects pause");
        vm.Queue.Add(first);
        vm.PlayCommand.Execute(null);
        output = backend.Last!;
        output.ReadAhead(TimeSpan.FromSeconds(1.2));
        vm.Queue.Clear();
        Check(output.Disposed && vm.CurrentTrack == second && vm.IsQueueEmpty && vm.IsPlaying,
            "Removing a song during a buffered fade flushes stale audio and preserves current playback");
        vm.CrossfadeDurationSeconds = 7;
        vm.Volume = 42;
        Check(preferences.LoadCrossfade() == new CrossfadeOptions(true, 7) && preferences.LoadVolume().Volume == 42,
            "Crossfade options persist without overwriting other settings");
        using var restoredPlayer = new NAudioPlayer(new TestAudioBackend());
        using var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), restoredPlayer,
            uiPreferencesStore: new JsonUiPreferencesStore(path), monitorLibrary: false);
        Check(restored.IsCrossfadeEnabled && restored.CrossfadeDurationSeconds == 7 && restoredPlayer.CrossfadeOptions.Enabled,
            "Crossfade configuration survives restart");
        var view = new MusicPlayer.Views.SettingsView { DataContext = restored };
        view.Measure(new System.Windows.Size(900, 700));
        view.Arrange(new System.Windows.Rect(0, 0, 900, 700));
        view.UpdateLayout();
        var toggle = (System.Windows.Controls.CheckBox)view.FindName("CrossfadeEnabledCheckBox");
        var binding = toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!;
        binding.UpdateTarget();
        toggle.IsChecked = false;
        binding.UpdateSource();
        Check(!restored.IsCrossfadeEnabled && !restoredPlayer.CrossfadeOptions.Enabled, "Settings toggle controls crossfade playback");
    }
}
