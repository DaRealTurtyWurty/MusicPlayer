using Microsoft.Win32;
using System.Windows;

namespace MusicPlayer.Services;

public sealed class LyricsFilePicker : ILyricsFilePicker
{
    public string? PickLyricsFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose lyrics for this track",
            Filter = "Lyrics files (*.lrc;*.ttml;*.lyricsfile.yaml)|*.lrc;*.ttml;*.lyricsfile.yaml",
            CheckFileExists = true,
            Multiselect = false
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }
}
