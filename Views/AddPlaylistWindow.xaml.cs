using System.Windows;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class AddPlaylistWindow : Window
{
    public AddPlaylistWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => CreatePlaylistButton.Focus();
    }

    private void PlaylistAction_Click(object sender, RoutedEventArgs e)
    {
        // Button raises Click before executing its command, so close the dialog
        // before creating the playlist or opening the file/folder picker.
        DialogResult = true;
    }
}
