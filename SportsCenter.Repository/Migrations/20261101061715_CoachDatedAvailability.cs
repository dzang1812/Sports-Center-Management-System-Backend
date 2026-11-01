using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class CoachDatedAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoachAvailabilities_CoachId_DayOfWeek_StartTime",
                table: "CoachAvailabilities");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Date",
                table: "CoachAvailabilities",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachAvailabilities_CoachId_Date_StartTime",
                table: "CoachAvailabilities",
                columns: new[] { "CoachId", "Date", "StartTime" },
                unique: true,
                filter: "\"IsActive\" AND \"Date\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoachAvailabilities_CoachId_DayOfWeek_StartTime",
                table: "CoachAvailabilities",
                columns: new[] { "CoachId", "DayOfWeek", "StartTime" },
                unique: true,
                filter: "\"IsActive\" AND \"Date\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Dated PT availability is forward-only to preserve week boundaries and booking history.");
        }
    }
}
