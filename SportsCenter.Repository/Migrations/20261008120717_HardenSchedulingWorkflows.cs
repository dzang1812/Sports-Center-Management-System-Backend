using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class HardenSchedulingWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AttendanceFinalizedAt",
                table: "PtBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceFinalizedByUserId",
                table: "PtBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLateCancellation",
                table: "PtBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttendanceFinalizedAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceFinalizedByUserId",
                table: "ClassSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Capacity",
                table: "ClassSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClassName",
                table: "ClassSessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "ClassSessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NotificationRevision",
                table: "ClassSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NotificationRevision",
                table: "Classes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsLateCancellation",
                table: "ClassBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_AttendanceFinalizedByUserId",
                table: "PtBookings",
                column: "AttendanceFinalizedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_AttendanceFinalizedByUserId",
                table: "ClassSessions",
                column: "AttendanceFinalizedByUserId");

            migrationBuilder.Sql("""
                UPDATE "ClassSessions" AS session
                SET "ClassName" = class."Name",
                    "Level" = class."Level",
                    "Capacity" = class."Capacity"
                FROM "Classes" AS class
                WHERE session."ClassId" = class."Id";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClassSession_Capacity",
                table: "ClassSessions",
                sql: "\"Capacity\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_ClassSessions_Users_AttendanceFinalizedByUserId",
                table: "ClassSessions",
                column: "AttendanceFinalizedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PtBookings_Users_AttendanceFinalizedByUserId",
                table: "PtBookings",
                column: "AttendanceFinalizedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClassSessions_Users_AttendanceFinalizedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_PtBookings_Users_AttendanceFinalizedByUserId",
                table: "PtBookings");

            migrationBuilder.DropIndex(
                name: "IX_PtBookings_AttendanceFinalizedByUserId",
                table: "PtBookings");

            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_AttendanceFinalizedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClassSession_Capacity",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "AttendanceFinalizedAt",
                table: "PtBookings");

            migrationBuilder.DropColumn(
                name: "AttendanceFinalizedByUserId",
                table: "PtBookings");

            migrationBuilder.DropColumn(
                name: "IsLateCancellation",
                table: "PtBookings");

            migrationBuilder.DropColumn(
                name: "AttendanceFinalizedAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "AttendanceFinalizedByUserId",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "ClassName",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "NotificationRevision",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "NotificationRevision",
                table: "Classes");

            migrationBuilder.DropColumn(
                name: "IsLateCancellation",
                table: "ClassBookings");
        }
    }
}
