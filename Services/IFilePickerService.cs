namespace MusicPlayer.Services;

public interface IFilePickerService
{
    string? PickAudioFile();
    IReadOnlyList<string> PickAudioFiles();
    string? PickPlaylistFile();
}
