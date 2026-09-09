using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicPlayer;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckClearQueueConfirmation()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        vm.SelectedTrack = Track("Previously played");
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.SelectedTrack = Track("Current song");
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(Track("Upcoming A"));
        vm.Queue.Add(Track("Upcoming B"));
        var original = vm.QueueTimeline.ToArray();
        var window = new MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        vm.ClearQueueCommand.Execute(null);
        SaveMaintenancePreview(content, "clear-queue-confirmation.png", 884, 561);
        var dialog = (FrameworkElement)window.FindName("ClearQueueDialog");
        var main = (FrameworkElement)window.FindName("MainContent");
        Check(vm.IsClearQueueConfirmationOpen && vm.QueueTimeline.SequenceEqual(original) &&
              dialog.Visibility == Visibility.Visible && !main.IsEnabled,
            "Clear opens a modal and leaves the queue unchanged until confirmation");
        Check(((Button)window.FindName("CancelClearQueueButton")).IsDefault &&
              dialog.InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Escape && b.Command == vm.CancelClearQueueCommand),
            "Queue confirmation defaults to Cancel and supports Escape");
        vm.CancelClearQueueCommand.Execute(null);
        Check(!vm.IsClearQueueConfirmationOpen && vm.QueueTimeline.SequenceEqual(original) && main.IsEnabled,
            "Cancelling closes the modal without changing playback, history or upcoming songs");
        vm.ConfirmClearQueueCommand.Execute(null);
        Check(vm.Queue.Count == 2, "Confirmation cannot clear the queue when the dialog is closed");
        vm.ClearQueueCommand.Execute(null);
        player.End();
        Check(vm.ClearQueueConfirmationMessage.Contains("1 upcoming song") && vm.IsClearQueueConfirmationOpen,
            "Confirmation count stays accurate when playback advances while the dialog is open");
        var current = vm.CurrentTrack;
        var history = vm.QueueTimeline.Where(e => e.IsHistory).Select(e => e.Track).ToArray();
        vm.ConfirmClearQueueCommand.Execute(null);
        Check(vm.Queue.Count == 0 && !vm.IsClearQueueConfirmationOpen && vm.CurrentTrack == current && vm.IsPlaying &&
              vm.QueueTimeline.Where(e => e.IsHistory).Select(e => e.Track).SequenceEqual(history),
            "Confirming clears upcoming songs and preserves current playback and history");
        vm.ClearQueueCommand.Execute(null);
        Check(!vm.IsClearQueueConfirmationOpen, "An empty queue does not open a confirmation");
        vm.Queue.Add(Track("Last upcoming song"));
        vm.ClearQueueCommand.Execute(null);
        player.End();
        Check(!vm.IsClearQueueConfirmationOpen && main.IsEnabled,
            "The modal closes automatically if playback consumes the final upcoming song");
        window.DataContext = null;
        window.Close();
    }
}
