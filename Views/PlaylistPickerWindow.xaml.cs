using System.Windows;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class PlaylistPickerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Track[] _tracks;

    public PlaylistPickerWindow(MainViewModel viewModel, Track track)
    : this(viewModel, new[] { track }) { }

    public PlaylistPickerWindow(MainViewModel viewModel, IEnumerable<Track> tracks)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _tracks = tracks.ToArray();
        DataContext = viewModel;
        TrackTitle.Text = _tracks.Length == 1 ? _tracks[0].Title : $"{_tracks.Length} selected tracks";
        TrackTitle.ToolTip = TrackTitle.Text;
        Loaded += (_, _) => DestinationList.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is Playlist playlist && _viewModel.TryAddTracksToPlaylist(_tracks, playlist))
            DialogResult = true;
    }
}
