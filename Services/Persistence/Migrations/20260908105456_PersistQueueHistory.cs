using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistQueueHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackHistoryEntries",
                columns: table => new
                {
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<long>(type: "INTEGER", nullable: false),
                    Recycled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistoryEntries", x => x.Position);
                    table.CheckConstraint("CK_PlaybackHistoryEntries_Position", "Position >= 0");
                    table.ForeignKey(
                        name: "FK_PlaybackHistoryEntries_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistoryEntries_TrackId",
                table: "PlaybackHistoryEntries",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistoryEntries");
        }
    }
}
