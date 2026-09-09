using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackLibraryMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExplicitlyAddedToLibrary",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Older versions did not record import origins. Classify existing playlist members
            // as playlist-sourced, and preserve standalone library songs as explicit additions.
            migrationBuilder.Sql("UPDATE Tracks SET ExplicitlyAddedToLibrary = 1 WHERE NOT EXISTS (SELECT 1 FROM PlaylistEntries WHERE PlaylistEntries.TrackId = Tracks.Id)");

            migrationBuilder.AddColumn<bool>(
                name: "DiscoverNewTracks",
                table: "MusicFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            // Preserve discovery behavior for previously configured folders whose origin is unknown.
            migrationBuilder.Sql("UPDATE MusicFolders SET DiscoverNewTracks = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExplicitlyAddedToLibrary",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "DiscoverNewTracks",
                table: "MusicFolders");
        }
    }
}
