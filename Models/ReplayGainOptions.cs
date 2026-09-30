namespace MusicPlayer.Models;

public enum ReplayGainMode { Track, Album }

public sealed record ReplayGainOptions(bool Enabled = true, ReplayGainMode Mode = ReplayGainMode.Track,
    double PreampDb = 0, bool PreventClipping = true)
{
    public ReplayGainOptions Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : ReplayGainMode.Track,
        PreampDb = double.IsFinite(PreampDb) ? Math.Clamp(PreampDb, -12, 12) : 0
    };
}
