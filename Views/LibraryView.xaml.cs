using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class LibraryView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(LibraryView), new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    public LibraryView() => InitializeComponent();

    private void AddMusic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void LibraryView_SizeChanged(object sender, SizeChangedEventArgs e) => IsCompact = ActualWidth < 720;

    private void LibrarySearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
        vm.ClearLibrarySearchCommand.Execute(null);
        e.Handled = true;
    }

    private void TrackActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            ItemsControl.ContainerFromElement(TrackList, button) is not ListViewItem { ContextMenu: { } menu } row) return;
        row.IsSelected = true;
        row.Focus();
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Track_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListViewItem row) return;
        row.IsSelected = true;
        row.Focus();
        if (row.ContextMenu is { } menu)
        {
            menu.PlacementTarget = row;
            menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
        }
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Track track } && DataContext is MainViewModel vm)
        {
            vm.SelectedTrack = track;
            vm.AddToQueueCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Track track } && DataContext is MainViewModel vm)
        {
            vm.SelectedTrack = track;
            vm.PlayNextCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void TrackActions_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Track track } && DataContext is MainViewModel vm)
        {
            var picker = new PlaylistPickerWindow(vm, track) { Owner = Window.GetWindow(this) };
            picker.ShowDialog();
        }
        e.Handled = true;
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(TrackList, source) is ListViewItem &&
            DataContext is MainViewModel vm && vm.PlaySelectedTrackCommand.CanExecute(null))
            vm.PlaySelectedTrackCommand.Execute(null);
    }
}
