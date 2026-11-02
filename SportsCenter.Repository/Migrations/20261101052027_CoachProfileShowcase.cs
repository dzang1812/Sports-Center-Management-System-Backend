using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class CoachProfileShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExperienceYears",
                table: "CoachProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StudentAfterImageUrl",
                table: "CoachProfiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StudentBeforeImageUrl",
                table: "CoachProfiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StudentPhotosConsentedAt",
                table: "CoachProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StudentResultSummary",
                table: "CoachProfiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExperienceYears",
                table: "CoachProfiles");

            migrationBuilder.DropColumn(
                name: "StudentAfterImageUrl",
                table: "CoachProfiles");

            migrationBuilder.DropColumn(
                name: "StudentBeforeImageUrl",
                table: "CoachProfiles");

            migrationBuilder.DropColumn(
                name: "StudentPhotosConsentedAt",
                table: "CoachProfiles");

            migrationBuilder.DropColumn(
                name: "StudentResultSummary",
                table: "CoachProfiles");
        }
    }
}
