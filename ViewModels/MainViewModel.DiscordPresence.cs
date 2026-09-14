using CommunityToolkit.Mvvm.ComponentModel;
using MusicPlayer.Models;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private DiscordPresenceOptions _discordPresenceOptions = new();
    public DiscordPresenceOptions DiscordPresenceOptions => _discordPresenceOptions;

    [ObservableProperty] private string discordPresenceStatus = "Off";

    public void ConfigureDiscordPresence(DiscordPresenceOptions options)
    {
        options = options with { ApplicationId = options.ApplicationId?.Trim() ?? "" };
        if (options.Enabled && !DiscordPresenceOptions.IsValidApplicationId(options.ApplicationId))
            throw new ArgumentException("Enter a valid numeric Discord application ID.", nameof(options));
        if (_discordPresenceOptions == options) return;
        _discordPresenceOptions = options;
        _uiPreferencesStore?.SaveDiscordPresence(options);
        OnPropertyChanged(nameof(DiscordPresenceOptions));
    }
}
