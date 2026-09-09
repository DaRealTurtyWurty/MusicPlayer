using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MusicPlayer.Services.Persistence;

public sealed class MusicDbContextFactory : IDesignTimeDbContextFactory<MusicDbContext>
{
    public MusicDbContext CreateDbContext(string[] args)
    {
        // Scaffolding never opens the user's database. Database commands can supply -- --database-path <path>.
        var path = args is ["--database-path", var databasePath] ? databasePath : "music.design.db";
        return new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()).Options);
    }
}
