using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer.ViewModels;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Microsoft.Win32;

namespace MusicPlayer.Views;

public partial class PlaylistsView : UserControl
{
    public PlaylistsView() => InitializeComponent();

    private void Track_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem row) return;
        if (!row.IsSelected) { PlaylistTracks.SelectedItems.Clear(); row.IsSelected = true; }
        row.Focus();
    }

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Track track } && DataContext is MainViewModel vm)
            new PlaylistPickerWindow(vm, PlaylistTracks.SelectedItems.Contains(track) ? SelectedTracks() : [track]) { Owner = Window.GetWindow(this) }.ShowDialog();
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
    private void AddSelectedToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && SelectedTracks() is { Length: > 0 } tracks)
            new PlaylistPickerWindow(vm, tracks) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void PlaylistTracks_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key is Key.Up or Key.Down)
        { MoveSelected(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
    }
    private int[] SelectedIndices() => Enumerable.Range(0, PlaylistTracks.Items.Count)
        .Where(i => PlaylistTracks.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row
            ? row.IsSelected : PlaylistTracks.SelectedItems.Contains(PlaylistTracks.Items[i])).ToArray();
    private Track[] SelectedTracks() => SelectedIndices().Select(i => (Track)PlaylistTracks.Items[i]).ToArray();
    private void QueueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.QueueTracks(SelectedTracks());
    }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.RemovePlaylistTracks(SelectedIndices());
    }
    private void MoveSelectedUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveSelectedDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int direction)
    {
        if (DataContext is not MainViewModel vm) return;
        var indices = vm.MovePlaylistTracks(SelectedIndices(), direction);
        PlaylistTracks.SelectedItems.Clear();
        foreach (var index in indices)
        {
            PlaylistTracks.ScrollIntoView(PlaylistTracks.Items[index]);
            PlaylistTracks.UpdateLayout();
            if (PlaylistTracks.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row) row.IsSelected = true;
        }
    }
    private void ExportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedPlaylist: { } playlist } vm) return;
        var dialog = new SaveFileDialog { Filter = "UTF-8 playlist (*.m3u8)|*.m3u8", DefaultExt = ".m3u8",
            FileName = string.Concat(playlist.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            PlaylistExportService.Export(playlist, dialog.FileName, relativePaths: true);
            vm.PlaylistMessage = $"Exported {playlist.Tracks.Count} tracks to {dialog.FileName}.";
        }
        catch (Exception ex) { vm.PlaylistError = $"Could not export playlist: {ex.Message}"; }
    }

}
