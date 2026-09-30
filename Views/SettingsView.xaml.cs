using System.Windows;
using System.Windows.Controls;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => LoadDiscordSettings();
    }

    private void LoadDiscordSettings()
    {
        if (DataContext is not MainViewModel vm) return;
        EnabledCheckBox.IsChecked = vm.DiscordPresenceOptions.Enabled;
        ArtworkCheckBox.IsChecked = vm.DiscordPresenceOptions.LookupAlbumCovers;
        ApplicationIdBox.Text = vm.DiscordPresenceOptions.ApplicationId;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void SaveDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var options = new DiscordPresenceOptions(EnabledCheckBox.IsChecked == true,
            ApplicationIdBox.Text.Trim(), ArtworkCheckBox.IsChecked == true);
        if (options.Enabled && !DiscordPresenceOptions.IsValidApplicationId(options.ApplicationId))
        {
            ValidationText.Text = "Enter a valid numeric application ID to enable presence.";
            ValidationText.Visibility = Visibility.Visible;
            ApplicationIdBox.Focus();
            return;
        }
        vm.ConfigureDiscordPresence(options);
        ValidationText.Text = "Discord settings saved.";
        ValidationText.Visibility = Visibility.Visible;
    }
}
