using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Views;

public partial class LocateTrackWindow : Window
{
    public Track? SelectedMatch => Matches.SelectedItem as Track;

    public LocateTrackWindow(Track missing, IReadOnlyList<Track> matches)
    {
        InitializeComponent();
        SongDescription.Text = $"{missing.Title} · {missing.Artist}";
        Matches.ItemsSource = matches;
    }

    private void Matches_SelectionChanged(object sender, SelectionChangedEventArgs e) => UseFileButton.IsEnabled = SelectedMatch is not null;
    private void UseFile_Click(object sender, RoutedEventArgs e) { if (SelectedMatch is not null) DialogResult = true; }
    private void Matches_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Matches, source) is ListBoxItem && SelectedMatch is not null) DialogResult = true;
    }
}

public sealed class TrackMatchPicker : ITrackMatchPicker
{
    public Track? PickMatch(Track missing, IReadOnlyList<Track> matches)
    {
        var window = new LocateTrackWindow(missing, matches) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.SelectedMatch : null;
    }
}
