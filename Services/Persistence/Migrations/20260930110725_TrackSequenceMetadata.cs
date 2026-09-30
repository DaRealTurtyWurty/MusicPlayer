using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackSequenceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "DiscNumber",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "Genre",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "TrackNumber",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Year",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Genre",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "TrackNumber",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Tracks");
        }
    }
}
