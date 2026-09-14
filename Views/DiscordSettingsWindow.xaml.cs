using System.Windows;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class DiscordSettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public DiscordSettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        EnabledCheckBox.IsChecked = viewModel.DiscordPresenceOptions.Enabled;
        ArtworkCheckBox.IsChecked = viewModel.DiscordPresenceOptions.LookupAlbumCovers;
        ApplicationIdBox.Text = viewModel.DiscordPresenceOptions.ApplicationId;
        Loaded += (_, _) => EnabledCheckBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var options = new DiscordPresenceOptions(EnabledCheckBox.IsChecked == true, ApplicationIdBox.Text.Trim(),
            ArtworkCheckBox.IsChecked == true);
        if (options.Enabled && !DiscordPresenceOptions.IsValidApplicationId(options.ApplicationId))
        {
            ValidationText.Text = "Enter a valid numeric application ID to enable presence.";
            ValidationText.Visibility = Visibility.Visible;
            ApplicationIdBox.Focus();
            return;
        }
        _viewModel.ConfigureDiscordPresence(options);
        DialogResult = true;
    }
}
