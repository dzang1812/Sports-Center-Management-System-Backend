using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class CoachSpecialties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachProfileSport",
                columns: table => new
                {
                    CoachProfileUserId = table.Column<int>(type: "integer", nullable: false),
                    SportsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachProfileSport", x => new { x.CoachProfileUserId, x.SportsId });
                    table.ForeignKey(
                        name: "FK_CoachProfileSport_CoachProfiles_CoachProfileUserId",
                        column: x => x.CoachProfileUserId,
                        principalTable: "CoachProfiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CoachProfileSport_Sports_SportsId",
                        column: x => x.SportsId,
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachProfileSport_SportsId",
                table: "CoachProfileSport",
                column: "SportsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachProfileSport");
        }
    }
}
