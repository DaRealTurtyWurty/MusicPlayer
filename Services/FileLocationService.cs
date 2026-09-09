using System.Diagnostics;
using System.IO;

namespace MusicPlayer.Services;

public sealed class FileLocationService : IFileLocationService
{
    public void ShowFile(string filePath)
    {
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("The file is missing. Use Locate to find it again.", path);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
