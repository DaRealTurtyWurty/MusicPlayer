namespace MusicPlayer.Models;

public sealed record CrossfadeOptions(bool Enabled = false, double DurationSeconds = 5)
{
    public CrossfadeOptions Normalize() => this with
    {
        DurationSeconds = double.IsFinite(DurationSeconds) ? Math.Clamp(DurationSeconds, 1, 12) : 5
    };
}
