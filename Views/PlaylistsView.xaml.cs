using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer.ViewModels;
using MusicPlayer.Models;

namespace MusicPlayer.Views;

public partial class PlaylistsView : UserControl
{
    public PlaylistsView() => InitializeComponent();

    private void Track_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem row) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Track track } && DataContext is MainViewModel vm)
            new PlaylistPickerWindow(vm, track) { Owner = Window.GetWindow(this) }.ShowDialog();
        e.Handled = true;
    }

    private void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var dialog = new AddPlaylistWindow(vm) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }
    }

    private void PlaylistNameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox editor)
            Dispatcher.BeginInvoke(() =>
            {
                editor.Focus();
                editor.SelectAll();
            });
    }

    private void PlaylistTracks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(PlaylistTracks, source) is ListBoxItem &&
            DataContext is MainViewModel vm && vm.PlayPlaylistTrackCommand.CanExecute(null))
            vm.PlayPlaylistTrackCommand.Execute(null);
    }
}
