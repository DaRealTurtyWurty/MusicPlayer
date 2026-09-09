using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class MusicBrowserView : UserControl
{
    public MusicBrowserView() => InitializeComponent();

    private void MusicSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
        vm.ClearMusicSearchCommand.Execute(null);
        e.Handled = true;
    }

    private void MusicTrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(MusicTrackList, source) is ListViewItem &&
            DataContext is MainViewModel vm && vm.PlayBrowseTrackCommand.CanExecute(null))
            vm.PlayBrowseTrackCommand.Execute(null);
    }

    private void Track_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListViewItem row) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void TrackActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            ItemsControl.ContainerFromElement(MusicTrackList, button) is not ListViewItem { ContextMenu: { } menu } row) return;
        row.IsSelected = true;
        row.Focus();
        // Keep the row as the placement target: its Tag supplies the view model to the detached popup.
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void TrackActions_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: Models.Track track } && DataContext is MainViewModel vm)
            new PlaylistPickerWindow(vm, track) { Owner = Window.GetWindow(this) }.ShowDialog();
        e.Handled = true;
    }
}
