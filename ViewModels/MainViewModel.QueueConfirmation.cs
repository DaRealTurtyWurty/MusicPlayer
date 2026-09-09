using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmClearQueueCommand))]
    private bool isClearQueueConfirmationOpen;

    public string ClearQueueConfirmationMessage =>
        $"Remove {Queue.Count} upcoming {(Queue.Count == 1 ? "song" : "songs")} from the queue?";

    private bool CanConfirmClearQueue() => IsClearQueueConfirmationOpen && HasQueuedTracks();

    [RelayCommand(CanExecute = nameof(CanConfirmClearQueue))]
    private void ConfirmClearQueue()
    {
        if (!CanConfirmClearQueue()) return;
        Queue.Clear();
        IsClearQueueConfirmationOpen = false;
    }

    [RelayCommand]
    private void CancelClearQueue() => IsClearQueueConfirmationOpen = false;
}
