using Microsoft.Win32;

namespace MusicPlayer.Services;

public sealed class FilePickerService : IFilePickerService
{
    public IReadOnlyList<string> PickAudioFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add tracks to playlist",
            Filter = "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.ogg",
            Multiselect = true,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickPlaylistFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a playlist",
            Filter = "M3U playlists|*.m3u8;*.m3u",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickAudioFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a song",
            Filter = "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.ogg|All files|*.*"
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }
}
