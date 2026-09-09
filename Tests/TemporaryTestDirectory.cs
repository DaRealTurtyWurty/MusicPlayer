using System.IO;
using Microsoft.Data.Sqlite;

internal sealed class TemporaryTestDirectory : IDisposable
{
    public string Path { get; }

    public TemporaryTestDirectory(string category)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), category, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        // Declare this scope before the test's other using declarations so their
        // file handles are disposed first. SQLite also retains idle pooled handles.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
