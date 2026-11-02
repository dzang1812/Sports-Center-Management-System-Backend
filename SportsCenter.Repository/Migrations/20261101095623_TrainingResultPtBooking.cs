using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class TrainingResultPtBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PtBookingId",
                table: "TrainingResults",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrainingResults_PtBookingId",
                table: "TrainingResults",
                column: "PtBookingId");

            migrationBuilder.AddForeignKey(
                name: "FK_TrainingResults_PtBookings_PtBookingId",
                table: "TrainingResults",
                column: "PtBookingId",
                principalTable: "PtBookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TrainingResults_PtBookings_PtBookingId",
                table: "TrainingResults");

            migrationBuilder.DropIndex(
                name: "IX_TrainingResults_PtBookingId",
                table: "TrainingResults");

            migrationBuilder.DropColumn(
                name: "PtBookingId",
                table: "TrainingResults");
        }
    }
}
