using System.Diagnostics;
using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>Bounded, best-effort diagnostics; logging failure must not stop playback.</summary>
public sealed class DiagnosticLog : TraceListener
{
    private readonly object _gate = new();
    private readonly string _path;
    private const long MaximumBytes = 2 * 1024 * 1024;
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "logs");

    public DiagnosticLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"player-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        File.WriteAllText(_path, "MusicPlayer diagnostics\n");
        foreach (var file in new DirectoryInfo(directory).GetFiles("player-*.log")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
            try { file.Delete(); } catch (IOException) { }
    }

    public override void Write(string? message) => WriteLine(message);
    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            try
            {
                if (new FileInfo(_path).Length >= MaximumBytes) return;
                // Keep each entry bounded as exception messages can contain external input.
                var entry = message ?? "";
                if (entry.Length > 8192) entry = entry[..8192];
                File.AppendAllText(_path, $"{DateTime.UtcNow:O} {entry}\n", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Start()
    {
        try
        {
            Trace.Listeners.Add(new DiagnosticLog(DirectoryPath));
            Trace.TraceInformation($"Starting MusicPlayer {typeof(App).Assembly.GetName().Version}; OS {Environment.OSVersion}; .NET {Environment.Version}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
