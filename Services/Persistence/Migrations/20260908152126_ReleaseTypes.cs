using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleaseTypeTag",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            // Read the new metadata for existing files during the next normal background refresh.
            // The fingerprint is restored after reading, including for files without a release-type tag.
            migrationBuilder.Sql("UPDATE Tracks SET LastWriteTimeUtcTicks = NULL");

            migrationBuilder.CreateTable(
                name: "ReleaseTypes",
                columns: table => new
                {
                    ReleaseKey = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseTypes", x => x.ReleaseKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseTypes");

            migrationBuilder.DropColumn(
                name: "ReleaseTypeTag",
                table: "Tracks");
        }
    }
}
