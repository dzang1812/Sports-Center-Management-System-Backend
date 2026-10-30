using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class Flow2FundingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_FundingOrderId_Status",
                table: "PtBookings",
                columns: new[] { "FundingOrderId", "Status" },
                unique: true,
                filter: "\"FundingOrderId\" IS NOT NULL AND \"Status\" = 'Confirmed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PtBookings_FundingOrderId_Status",
                table: "PtBookings");

        }
    }
}
