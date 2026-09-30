using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(HistoryList, source) is ListViewItem item
            && item.DataContext is ListeningHistoryEntry entry && DataContext is MainViewModel vm)
            vm.PlayHistoryEntryCommand.Execute(entry);
    }
}
