using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddClassSessionCoach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoachId",
                table: "ClassSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ClassSessions" s SET "CoachId" = c."CoachId"
                FROM "Classes" c WHERE c."Id" = s."ClassId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_CoachId",
                table: "ClassSessions",
                column: "CoachId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClassSessions_Users_CoachId",
                table: "ClassSessions",
                column: "CoachId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClassSessions_Users_CoachId",
                table: "ClassSessions");

            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_CoachId",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CoachId",
                table: "ClassSessions");
        }
    }
}
