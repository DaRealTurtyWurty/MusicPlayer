using System.IO;
using Microsoft.Data.Sqlite;

internal static class TemporaryTestDirectoryTests
{
    public static void Run()
    {
        using var sibling = new TemporaryTestDirectory("MusicPlayerCleanupTests");
        var marker = System.IO.Path.Combine(sibling.Path, "keep.txt");
        File.WriteAllText(marker, "A different test owns this file.");

        foreach (var fail in new[] { false, true })
        {
            string? path = null;
            var expectedFailure = new InvalidOperationException("Simulated assertion failure");
            try
            {
                using var temporaryDirectory = new TemporaryTestDirectory("MusicPlayerCleanupTests");
                path = temporaryDirectory.Path;
                Directory.CreateDirectory(System.IO.Path.Combine(path, "nested"));
                using var file = File.Create(System.IO.Path.Combine(path, "nested", "fixture.wav"));
                file.Write(new byte[1024]);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = System.IO.Path.Combine(path, "music.db"),
                    Pooling = true
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Fixture (Id INTEGER PRIMARY KEY)";
                command.ExecuteNonQuery();
                if (fail)
                    throw expectedFailure;
            }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, expectedFailure))
            {
            }

            if (path is null || Directory.Exists(path))
                throw new InvalidOperationException($"Temporary files survived a {(fail ? "failed" : "successful")} test.");
            if (!File.Exists(marker))
                throw new InvalidOperationException("Cleanup removed another test's files.");
        }
    }
}
