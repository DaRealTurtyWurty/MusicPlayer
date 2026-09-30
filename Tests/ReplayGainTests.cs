using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using NAudio.Wave;

internal static partial class Program
{
    private static void CheckReplayGain()
    {
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .0001;
        var options = new ReplayGainOptions();
        var tags = new ReplayGainMetadata(-6, .5, -12, .8);
        Check(Near(tags.GetScale(options), Math.Pow(10, -.3)), "Track gain converts decibels to amplitude");
        Check(Near(tags.GetScale(options with { Mode = ReplayGainMode.Album }), Math.Pow(10, -.6)), "Album mode selects album gain");
        Check(Near(new ReplayGainMetadata(-6).GetScale(options with { Mode = ReplayGainMode.Album }), tags.GetScale(options)), "Missing album gain falls back to track gain");
        Check(tags.GetScale(options with { Enabled = false }) == 1 && new ReplayGainMetadata().GetScale(options with { PreampDb = 12 }) == 1,
            "Disabled ReplayGain and untagged tracks retain unity gain");
        Check(Near(tags.GetScale(options with { PreampDb = 6 }), 1), "Preamp combines with tagged gain");
        Check(Near(new ReplayGainMetadata(12, .8).GetScale(options), 1.25), "Peak metadata reduces boost to avoid clipping");
        Check(new ReplayGainMetadata(12, .8).GetScale(options with { PreventClipping = false }) > 3, "Clipping protection can be disabled");
        Check(new ReplayGainOptions(Mode: (ReplayGainMode)999, PreampDb: double.NaN).Normalize() == options,
            "Invalid options normalize safely");

        float[] samples = new float[2];
        var mutableOptions = options;
        var provider = new ReplayGainSampleProvider(new TestSamples([.5f, -.5f]), tags, () => mutableOptions);
        provider.Read(samples.AsSpan(0, 1));
        mutableOptions = options with { Enabled = false };
        provider.Read(samples.AsSpan(1, 1));
        Check(Near(samples[0], .5 * tags.GetScale(options)) && samples[1] == -.5f, "Settings changes affect the next decoded samples");
        var protectedProvider = new ReplayGainSampleProvider(new TestSamples([.8f, -.8f]), new(20), () => options);
        protectedProvider.Read(samples);
        Check(samples.SequenceEqual(new float[] { 1, -1 }), "Missing peak tags still protect boosted samples from clipping");
        var gapless = new GaplessSampleProvider(new ReplayGainSampleProvider(new TestSamples([.5f]), tags, () => options));
        gapless.SetNext(new ReplayGainSampleProvider(new TestSamples([.5f]), new(0), () => options));
        gapless.Read(samples);
        Check(Near(samples[0], .5 * tags.GetScale(options)) && samples[1] == .5f, "Gain switches at the exact gapless sample boundary");

        using var temporary = new TemporaryTestDirectory("MusicPlayerReplayGainTests");
        var preferencePath = Path.Combine(temporary.Path, "preferences.json");
        var preferences = new JsonUiPreferencesStore(preferencePath);
        Check(preferences.LoadReplayGain() == options, "New preferences enable ReplayGain and clipping protection");
        File.WriteAllText(preferencePath, "{\"Volume\":35}");
        Check(preferences.LoadReplayGain() == options, "Existing preferences enable ReplayGain by default");
        var backend = new TestAudioBackend();
        using var player = new NAudioPlayer(backend);
        using (var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player,
                   uiPreferencesStore: preferences, monitorLibrary: false))
        {
            vm.SelectedReplayGainMode = ReplayGainMode.Album;
            vm.ReplayGainPreampDb = 3;
            vm.ReplayGainPreventClipping = false;
            vm.IsReplayGainEnabled = false;
            vm.Volume = 42;
            Check(player.ReplayGainOptions == new ReplayGainOptions(false, ReplayGainMode.Album, 3, false)
                && preferences.LoadReplayGain() == player.ReplayGainOptions && preferences.LoadVolume().Volume == 42,
                "View model applies and persists all options without overwriting volume");
        }
        using var restoredPlayer = new NAudioPlayer(new TestAudioBackend());
        using var restored = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), restoredPlayer,
            uiPreferencesStore: new JsonUiPreferencesStore(preferencePath), monitorLibrary: false);
        Check(!restored.IsReplayGainEnabled && restored.SelectedReplayGainMode == ReplayGainMode.Album
            && restored.ReplayGainPreampDb == 3 && !restored.ReplayGainPreventClipping,
            "ReplayGain settings survive restart");

        var audioPath = Path.Combine(temporary.Path, "tagged.wav");
        using (var writer = new WaveFileWriter(audioPath, new WaveFormat(44100, 16, 1)))
            writer.WriteSamples(Enumerable.Repeat(.5f, 44100).ToArray(), 0, 44100);
        using (var file = TagLib.File.Create(audioPath))
        {
            file.Tag.ReplayGainTrackGain = -6;
            file.Tag.ReplayGainTrackPeak = .5;
            file.Tag.ReplayGainAlbumGain = -12;
            file.Tag.ReplayGainAlbumPeak = .5;
            file.Save();
        }
        Check(ReplayGainMetadata.Read(audioPath).TrackGain == -6 && ReplayGainMetadata.Read(audioPath).AlbumGain == -12,
            "Embedded track and album tags are read from disk");
        var playbackBackend = new TestAudioBackend();
        using var playbackPlayer = new NAudioPlayer(playbackBackend);
        playbackPlayer.ReplayGainOptions = options;
        playbackPlayer.Volume = .5f;
        playbackPlayer.Load(audioPath);
        float ReadOutputSample() => playbackBackend.Last!.ReadSample();
        Check(Near(ReadOutputSample(), .25 * Math.Pow(10, -.3)), "Actual playback combines ReplayGain and user volume");
        playbackPlayer.ReplayGainOptions = options with { Mode = ReplayGainMode.Album };
        playbackPlayer.Seek(TimeSpan.Zero);
        Check(Near(ReadOutputSample(), .25 * Math.Pow(10, -.6)), "Seek rebuilds output with album gain intact");
        playbackPlayer.SelectOutputDevice("B");
        Check(Near(ReadOutputSample(), .25 * Math.Pow(10, -.6)), "Device recovery preserves ReplayGain");

        var view = new MusicPlayer.Views.SettingsView { DataContext = restored };
        view.Measure(new System.Windows.Size(900, 700));
        view.Arrange(new System.Windows.Rect(0, 0, 900, 700));
        view.UpdateLayout();
        var toggle = (System.Windows.Controls.CheckBox)view.FindName("ReplayGainEnabledCheckBox");
        toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateTarget();
        toggle.IsChecked = true;
        toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
        Check(restored.IsReplayGainEnabled && restoredPlayer.ReplayGainOptions.Enabled, "Settings toggle binds to playback configuration");
    }
}
