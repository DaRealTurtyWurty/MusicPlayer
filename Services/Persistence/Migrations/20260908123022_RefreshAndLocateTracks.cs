using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefreshAndLocateTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FileSize",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMissing",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LastWriteTimeUtcTicks",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicFolders",
                columns: table => new
                {
                    PathKey = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeSubdirectories = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicFolders", x => x.PathKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusicFolders");

            migrationBuilder.DropColumn(
                name: "FileSize",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsMissing",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "LastWriteTimeUtcTicks",
                table: "Tracks");
        }
    }
}
