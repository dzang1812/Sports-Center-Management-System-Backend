using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class ClassSessionSelfCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CheckInCodeExpiresAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckInCodeHash",
                table: "ClassSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckInCodeIssuedAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckInCodeIssuedByUserId",
                table: "ClassSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Attendances",
                type: "text",
                nullable: false,
                defaultValue: "LegacyCoachMarked");

            migrationBuilder.CreateTable(
                name: "CoachSessionAttendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    CoachId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachSessionAttendances", x => x.Id);
                    table.CheckConstraint("CK_CoachSessionAttendance_Status", "\"Status\" IN ('Present', 'Late')");
                    table.ForeignKey(
                        name: "FK_CoachSessionAttendances_ClassSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClassSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoachSessionAttendances_Users_CoachId",
                        column: x => x.CoachId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_CheckInCodeIssuedByUserId",
                table: "ClassSessions",
                column: "CheckInCodeIssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachSessionAttendances_CoachId",
                table: "CoachSessionAttendances",
                column: "CoachId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachSessionAttendances_SessionId_CoachId",
                table: "CoachSessionAttendances",
                columns: new[] { "SessionId", "CoachId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClassSessions_Users_CheckInCodeIssuedByUserId",
                table: "ClassSessions",
                column: "CheckInCodeIssuedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClassSessions_Users_CheckInCodeIssuedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropTable(
                name: "CoachSessionAttendances");

            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_CheckInCodeIssuedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CheckInCodeExpiresAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CheckInCodeHash",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CheckInCodeIssuedAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CheckInCodeIssuedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Attendances");
        }
    }
}
