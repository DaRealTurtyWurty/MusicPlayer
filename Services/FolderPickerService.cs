using Microsoft.Win32;

namespace MusicPlayer.Services;

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickMusicFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your music folder",
            Multiselect = false
        };

        return dialog.ShowDialog() == true
            ? dialog.FolderName
            : null;
    }
}