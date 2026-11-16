using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class Flow2Commerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PtSinglePrice",
                table: "Sports",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SubscriptionId",
                table: "PtBookings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "FundingOrderId",
                table: "PtBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FundingSource",
                table: "PtBookings",
                type: "text",
                nullable: false,
                defaultValue: "LegacyCredit");

            migrationBuilder.AddColumn<bool>(
                name: "FundingTransferred",
                table: "PtBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProviderReference",
                table: "Payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "Payments",
                type: "text",
                nullable: false,
                defaultValue: "LegacySubscription");

            migrationBuilder.AddColumn<bool>(
                name: "EverUsedPtCredit",
                table: "MemberSubscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PurchaseOrderId",
                table: "MemberSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotCoachId",
                table: "MemberSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotPtCredits",
                table: "MemberSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotSportId",
                table: "MemberSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacesSessionId",
                table: "ClassSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingMode",
                table: "Classes",
                type: "text",
                nullable: false,
                defaultValue: "LegacySession");

            migrationBuilder.AddColumn<decimal>(
                name: "CourseFee",
                table: "Classes",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CourseStartsAt",
                table: "Classes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrollmentClosesAt",
                table: "Classes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedSessions",
                table: "Classes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Classes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderId",
                table: "ClassEnrollments",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SubscriptionId",
                table: "ClassBookings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "EnrollmentId",
                table: "ClassBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommerceOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    RefundedAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOrders", x => x.Id);
                    table.CheckConstraint("CK_Order_Refund", "\"PaidAmount\" > 0 AND \"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"PaidAmount\"");
                    table.ForeignKey(
                        name: "FK_CommerceOrders_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommerceOrders_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemberWallets",
                columns: table => new
                {
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberWallets", x => x.MemberId);
                    table.CheckConstraint("CK_Wallet_Nonnegative", "\"Balance\" >= 0 AND \"Balance\" = trunc(\"Balance\")");
                    table.ForeignKey(
                        name: "FK_MemberWallets_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CourseEntitlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnrollmentId = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<int>(type: "integer", nullable: false),
                    OriginalSessionId = table.Column<int>(type: "integer", nullable: false),
                    CurrentSessionId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Refunded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseEntitlements_ClassEnrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "ClassEnrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseEntitlements_ClassSessions_CurrentSessionId",
                        column: x => x.CurrentSessionId,
                        principalTable: "ClassSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseEntitlements_ClassSessions_OriginalSessionId",
                        column: x => x.OriginalSessionId,
                        principalTable: "ClassSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseEntitlements_CommerceOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalletEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    OrderId = table.Column<int>(type: "integer", nullable: true),
                    PaymentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletEntries", x => x.Id);
                    table.CheckConstraint("CK_WalletEntry_Vnd", "\"Amount\" <> 0 AND \"Amount\" = trunc(\"Amount\") AND \"BalanceAfter\" >= 0");
                    table.ForeignKey(
                        name: "FK_WalletEntries_CommerceOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletEntries_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletEntries_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletEntries_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_FundingOrderId",
                table: "PtBookings",
                column: "FundingOrderId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PtBooking_Source",
                table: "PtBookings",
                sql: "(\"FundingSource\" = 'Wallet' AND \"SubscriptionId\" IS NULL AND \"FundingOrderId\" IS NOT NULL) OR (\"FundingSource\" <> 'Wallet' AND \"SubscriptionId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderReference",
                table: "Payments",
                column: "ProviderReference",
                unique: true,
                filter: "\"ProviderReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MemberSubscriptions_PurchaseOrderId",
                table: "MemberSubscriptions",
                column: "PurchaseOrderId",
                unique: true,
                filter: "\"PurchaseOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_ReplacesSessionId",
                table: "ClassSessions",
                column: "ReplacesSessionId",
                unique: true,
                filter: "\"ReplacesSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassEnrollments_OrderId",
                table: "ClassEnrollments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_EnrollmentId",
                table: "ClassBookings",
                column: "EnrollmentId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClassBooking_Source",
                table: "ClassBookings",
                sql: "(\"SubscriptionId\" IS NOT NULL) <> (\"EnrollmentId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_ActorId",
                table: "CommerceOrders",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_MemberId",
                table: "CommerceOrders",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceRequests_ActorId_Operation_Key",
                table: "CommerceRequests",
                columns: new[] { "ActorId", "Operation", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseEntitlements_CurrentSessionId",
                table: "CourseEntitlements",
                column: "CurrentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseEntitlements_EnrollmentId",
                table: "CourseEntitlements",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseEntitlements_OrderId_OriginalSessionId",
                table: "CourseEntitlements",
                columns: new[] { "OrderId", "OriginalSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseEntitlements_OriginalSessionId",
                table: "CourseEntitlements",
                column: "OriginalSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletEntries_ActorId",
                table: "WalletEntries",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletEntries_MemberId",
                table: "WalletEntries",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletEntries_OrderId",
                table: "WalletEntries",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletEntries_PaymentId",
                table: "WalletEntries",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletEntries_Reference",
                table: "WalletEntries",
                column: "Reference",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClassBookings_ClassEnrollments_EnrollmentId",
                table: "ClassBookings",
                column: "EnrollmentId",
                principalTable: "ClassEnrollments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClassEnrollments_CommerceOrders_OrderId",
                table: "ClassEnrollments",
                column: "OrderId",
                principalTable: "CommerceOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClassSessions_ClassSessions_ReplacesSessionId",
                table: "ClassSessions",
                column: "ReplacesSessionId",
                principalTable: "ClassSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MemberSubscriptions_CommerceOrders_PurchaseOrderId",
                table: "MemberSubscriptions",
                column: "PurchaseOrderId",
                principalTable: "CommerceOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PtBookings_CommerceOrders_FundingOrderId",
                table: "PtBookings",
                column: "FundingOrderId",
                principalTable: "CommerceOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Commerce history cannot be rolled back destructively. Restore an approved backup or use a forward-only migration.");
        }
    }
}
