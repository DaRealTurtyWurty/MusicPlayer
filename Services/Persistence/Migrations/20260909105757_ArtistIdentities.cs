using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtistIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetadataVersion",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzArtistId",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArtistIdentities",
                columns: table => new
                {
                    LookupKey = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MusicBrainzId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistIdentities", x => x.LookupKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistIdentities");

            migrationBuilder.DropColumn(
                name: "MetadataVersion",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzArtistId",
                table: "Tracks");
        }
    }
}
