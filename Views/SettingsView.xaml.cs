using System.Windows;
using System.Windows.Controls;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;
using MusicPlayer.Services;

namespace MusicPlayer.Views;

public partial class SettingsView : UserControl
{
    private async void BackupLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => DatabaseRecovery.Backup(SqliteMusicStore.DefaultDatabasePath));
            BackupStatus.Text = $"Backup saved to {path}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Manual backup failed: {ex}");
            BackupStatus.Text = "Backup failed. Check the diagnostic logs in %LOCALAPPDATA%\\MusicPlayer\\logs.";
        }
        finally { button.IsEnabled = true; }
    }
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
