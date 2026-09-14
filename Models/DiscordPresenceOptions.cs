using System.Globalization;

namespace MusicPlayer.Models;

public sealed record DiscordPresenceOptions(bool Enabled = false,
    string ApplicationId = DiscordPresenceOptions.DefaultApplicationId, bool LookupAlbumCovers = true)
{
    public const string DefaultApplicationId = "1549010425205497987";

    public static bool IsValidApplicationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit) &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
}
