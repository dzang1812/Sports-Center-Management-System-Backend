using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class ExerciseMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultReps",
                table: "Exercises",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultSets",
                table: "Exercises",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Equipment",
                table: "Exercises",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "Exercises",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestSeconds",
                table: "Exercises",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoUrl",
                table: "Exercises",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultReps",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "DefaultSets",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "Equipment",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "RestSeconds",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "VideoUrl",
                table: "Exercises");
        }
    }
}
