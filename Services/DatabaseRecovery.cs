using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MusicPlayer.Services;

/// <summary>SQLite online snapshots and explicit, offline recovery. Never reset a corrupt library.</summary>
public static class DatabaseRecovery
{
    public static string Backup(string databasePath, string reason = "manual")
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"music-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{reason}.db");
        var temporary = path + ".tmp";
        try
        {
            using (var source = Connect(databasePath, SqliteOpenMode.ReadOnly))
            using (var destination = Connect(temporary, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(destination);
            Validate(temporary);
            File.Move(temporary, path);
            // Upgrade snapshots are retained separately from routine launch snapshots.
            if (reason == "startup")
                foreach (var old in new DirectoryInfo(directory).GetFiles("*-startup.db")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Skip(7)) old.Delete();
            Trace.TraceInformation($"Database backup completed ({reason}).");
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(string path)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Database integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("Database has broken library references.");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Tracks', 'Playlists', 'StorageStates', '__EFMigrationsHistory')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 4)
            throw new InvalidDataException("This file is not a MusicPlayer library backup.");
    }

    /// <summary>Caller must ensure all application connections are closed.</summary>
    public static string Restore(string backupPath, string databasePath)
    {
        backupPath = Path.GetFullPath(backupPath);
        databasePath = Path.GetFullPath(databasePath);
        if (string.Equals(backupPath, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a separate backup file.");
        Validate(backupPath);
        var directory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, $"restore-{Guid.NewGuid():N}.tmp");
        var archive = Path.Combine(directory, "recovery", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var moved = new List<(string Original, string Archived)>();
        try
        {
            using (var source = Connect(backupPath, SqliteOpenMode.ReadOnly))
            using (var target = Connect(stage, SqliteOpenMode.ReadWriteCreate)) source.BackupDatabase(target);
            Validate(stage);
            SqliteConnection.ClearAllPools();
            Directory.CreateDirectory(archive);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var original = databasePath + suffix;
                if (!File.Exists(original)) continue;
                var archived = Path.Combine(archive, Path.GetFileName(original));
                File.Move(original, archived);
                moved.Add((original, archived));
            }
            File.Move(stage, databasePath);
            Trace.TraceInformation("Library restored; previous database preserved in recovery directory.");
            return archive;
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse()) File.Move(item.Archived, item.Original);
            throw;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
