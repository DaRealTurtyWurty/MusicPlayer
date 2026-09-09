using System.Windows;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class PlaylistPickerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Track _track;

    public PlaylistPickerWindow(MainViewModel viewModel, Track track)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _track = track;
        DataContext = viewModel;
        TrackTitle.Text = track.Title;
        TrackTitle.ToolTip = track.Title;
        Loaded += (_, _) => DestinationList.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is Playlist playlist && _viewModel.TryAddTrackToPlaylist(_track, playlist))
            DialogResult = true;
    }
}
