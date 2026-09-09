using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMusicLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PathKey = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: true),
                    Album = table.Column<string>(type: "TEXT", nullable: true),
                    DurationTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.CheckConstraint("CK_Tracks_Duration", "DurationTicks >= 0");
                });

            migrationBuilder.CreateTable(
                name: "PlaylistEntries",
                columns: table => new
                {
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistEntries", x => new { x.PlaylistId, x.Position });
                    table.CheckConstraint("CK_PlaylistEntries_Position", "Position >= 0");
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_TrackId",
                table: "PlaylistEntries",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Artist_Album",
                table: "Tracks",
                columns: new[] { "Artist", "Album" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_PathKey",
                table: "Tracks",
                column: "PathKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistEntries");

            migrationBuilder.DropTable(
                name: "StorageStates");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropTable(
                name: "Tracks");
        }
    }
}
