using System.IO;

namespace MusicPlayer.Services;

// Watcher callbacks only request a debounced refresh; all library changes happen on the UI dispatcher.
public sealed class LibraryFileMonitor(Action changed) : IDisposable
{
    private readonly Dictionary<WatchedMusicFolder, FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private bool _disposed;

    public void Configure(IReadOnlyList<WatchedMusicFolder> folders)
    {
        lock (_gate)
        {
            if (!_disposed) ConfigureCore(folders);
        }
    }

    private void ConfigureCore(IReadOnlyList<WatchedMusicFolder> folders)
    {
        foreach (var removed in _watchers.Keys.Where(f => !folders.Contains(f) || !Directory.Exists(f.Path)).ToArray())
        {
            _watchers[removed].Dispose();
            _watchers.Remove(removed);
        }
        foreach (var folder in folders)
        {
            if (_watchers.ContainsKey(folder) || !Directory.Exists(folder.Path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = folder.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += (_, _) => changed();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(folder, watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => changed();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
