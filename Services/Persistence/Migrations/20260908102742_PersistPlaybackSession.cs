using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Services.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistPlaybackSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentTrackId = table.Column<long>(type: "INTEGER", nullable: true),
                    PositionTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessions", x => x.Id);
                    table.CheckConstraint("CK_PlaybackSessions_Id", "Id = 1");
                    table.CheckConstraint("CK_PlaybackSessions_Position", "PositionTicks >= 0");
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_Tracks_CurrentTrackId",
                        column: x => x.CurrentTrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QueueEntries",
                columns: table => new
                {
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueEntries", x => x.Position);
                    table.CheckConstraint("CK_QueueEntries_Position", "Position >= 0");
                    table.ForeignKey(
                        name: "FK_QueueEntries_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_CurrentTrackId",
                table: "PlaybackSessions",
                column: "CurrentTrackId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueEntries_TrackId",
                table: "QueueEntries",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackSessions");

            migrationBuilder.DropTable(
                name: "QueueEntries");
        }
    }
}
