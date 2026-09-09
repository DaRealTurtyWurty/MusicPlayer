using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Threading;
using MusicPlayer.ViewModels;
using MusicPlayer.Models;
using System.Windows.Controls.Primitives;

namespace MusicPlayer.Views;

public partial class QueuePanel : UserControl
{
    private MainViewModel? _viewModel;

    public QueuePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachViewModel();
        Unloaded += (_, _) => DetachViewModel();
        DataContextChanged += (_, _) => { if (IsLoaded) AttachViewModel(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScrollToCurrent(); };
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnPlaybackChanged;
        ScrollToCurrent();
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnPlaybackChanged;
        _viewModel = null;
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentTrack)) ScrollToCurrent();
    }

    private void ScrollToCurrent()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsVisible && DataContext is MainViewModel vm &&
                vm.QueueTimeline.FirstOrDefault(entry => entry.IsCurrent) is { } current)
                QueueList.ScrollIntoView(current);
        });
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(QueueList, source) is ListBoxItem &&
            DataContext is MainViewModel vm && vm.PlayQueueEntryCommand.CanExecute(null))
            vm.PlayQueueEntryCommand.Execute(null);
    }

    private void QueueEntry_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: QueueEntry entry, ContextMenu: { } menu } row ||
            DataContext is not MainViewModel vm) return;
        vm.SelectedQueueEntry = entry;
        row.IsSelected = true;
        row.Focus();
        menu.PlacementTarget = row;
        menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
    }

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: Models.Track track } && DataContext is MainViewModel vm)
            new PlaylistPickerWindow(vm, track) { Owner = Window.GetWindow(this) }.ShowDialog();
        e.Handled = true;
    }
}
