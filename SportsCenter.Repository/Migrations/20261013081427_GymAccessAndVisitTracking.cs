using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class GymAccessAndVisitTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CheckIns_MemberId",
                table: "CheckIns");

            migrationBuilder.AddColumn<bool>(
                name: "IncludesGymAccess",
                table: "MemberSubscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DurationMonths",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesGymAccess",
                table: "MembershipPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GymCardCode",
                table: "MemberProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckInSource",
                table: "CheckIns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "ReceptionDesk");

            migrationBuilder.AddColumn<string>(
                name: "CheckOutSource",
                table: "CheckIns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckedOutAt",
                table: "CheckIns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckedOutById",
                table: "CheckIns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionId",
                table: "CheckIns",
                type: "integer",
                nullable: true);

            // Legacy rows represented completed check-ins because the old schema had no checkout.
            // Close them before enforcing one open visit per member.
            migrationBuilder.Sql("""
                UPDATE "CheckIns"
                SET "CheckedOutAt" = "CheckedInAt",
                    "CheckInSource" = 'Legacy'
                WHERE "CheckedOutAt" IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MembershipPlan_Duration",
                table: "MembershipPlans",
                sql: "(\"DurationDays\" > 0 AND \"DurationMonths\" = 0) OR (\"DurationDays\" = 0 AND \"DurationMonths\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_MemberProfiles_GymCardCode",
                table: "MemberProfiles",
                column: "GymCardCode",
                unique: true,
                filter: "\"GymCardCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_CheckedOutById",
                table: "CheckIns",
                column: "CheckedOutById");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_MemberId",
                table: "CheckIns",
                column: "MemberId",
                unique: true,
                filter: "\"CheckedOutAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_SubscriptionId",
                table: "CheckIns",
                column: "SubscriptionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CheckIn_CheckOutTime",
                table: "CheckIns",
                sql: "\"CheckedOutAt\" IS NULL OR \"CheckedOutAt\" >= \"CheckedInAt\"");

            migrationBuilder.AddForeignKey(
                name: "FK_CheckIns_MemberSubscriptions_SubscriptionId",
                table: "CheckIns",
                column: "SubscriptionId",
                principalTable: "MemberSubscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CheckIns_Users_CheckedOutById",
                table: "CheckIns",
                column: "CheckedOutById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CheckIns_MemberSubscriptions_SubscriptionId",
                table: "CheckIns");

            migrationBuilder.DropForeignKey(
                name: "FK_CheckIns_Users_CheckedOutById",
                table: "CheckIns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MembershipPlan_Duration",
                table: "MembershipPlans");

            migrationBuilder.DropIndex(
                name: "IX_MemberProfiles_GymCardCode",
                table: "MemberProfiles");

            migrationBuilder.DropIndex(
                name: "IX_CheckIns_CheckedOutById",
                table: "CheckIns");

            migrationBuilder.DropIndex(
                name: "IX_CheckIns_MemberId",
                table: "CheckIns");

            migrationBuilder.DropIndex(
                name: "IX_CheckIns_SubscriptionId",
                table: "CheckIns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CheckIn_CheckOutTime",
                table: "CheckIns");

            migrationBuilder.DropColumn(
                name: "IncludesGymAccess",
                table: "MemberSubscriptions");

            migrationBuilder.DropColumn(
                name: "DurationMonths",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "IncludesGymAccess",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "GymCardCode",
                table: "MemberProfiles");

            migrationBuilder.DropColumn(
                name: "CheckInSource",
                table: "CheckIns");

            migrationBuilder.DropColumn(
                name: "CheckOutSource",
                table: "CheckIns");

            migrationBuilder.DropColumn(
                name: "CheckedOutAt",
                table: "CheckIns");

            migrationBuilder.DropColumn(
                name: "CheckedOutById",
                table: "CheckIns");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "CheckIns");

            migrationBuilder.CreateIndex(
                name: "IX_CheckIns_MemberId",
                table: "CheckIns",
                column: "MemberId");
        }
    }
}
