using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class ClassBookingAndScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_ClassId",
                table: "ClassSessions");

            migrationBuilder.DropIndex(
                name: "IX_Attendances_SessionId_MemberId",
                table: "Attendances");

            migrationBuilder.AddColumn<bool>(
                name: "HasClasses",
                table: "Sports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasPt",
                table: "Sports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Sports",
                type: "text",
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Rooms",
                type: "text",
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "Notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemainingPtSessions",
                table: "MemberSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesClasses",
                table: "MembershipPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PtSessionCredits",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SportId",
                table: "MembershipPlans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "ClassSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledByUserId",
                table: "ClassSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AlterColumn<int>(
                name: "CoachId",
                table: "Classes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "RoomId",
                table: "Classes",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SessionId",
                table: "Attendances",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "PtBookingId",
                table: "Attendances",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClassBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    PerformedByUserId = table.Column<int>(type: "integer", nullable: false),
                    SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<int>(type: "integer", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassBookings_ClassSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClassSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassBookings_MemberSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "MemberSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassBookings_Users_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassBookings_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassBookings_Users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CoachAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoachId = table.Column<int>(type: "integer", nullable: false),
                    SportId = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<string>(type: "text", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachAvailabilities", x => x.Id);
                    table.CheckConstraint("CK_CoachAvailability_Times", "\"EndTime\" > \"StartTime\"");
                    table.ForeignKey(
                        name: "FK_CoachAvailabilities_Sports_SportId",
                        column: x => x.SportId,
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CoachAvailabilities_Users_CoachId",
                        column: x => x.CoachId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    CoachId = table.Column<int>(type: "integer", nullable: false),
                    SportId = table.Column<int>(type: "integer", nullable: false),
                    RoomId = table.Column<int>(type: "integer", nullable: true),
                    SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    PerformedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<int>(type: "integer", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    CreditRefunded = table.Column<bool>(type: "boolean", nullable: false),
                    CreditRefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtBookings", x => x.Id);
                    table.CheckConstraint("CK_PtBooking_OneHour", "\"EndTime\" = \"StartTime\" + interval '1 hour'");
                    table.ForeignKey(
                        name: "FK_PtBookings_MemberSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "MemberSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Sports_SportId",
                        column: x => x.SportId,
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Users_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Users_CoachId",
                        column: x => x.CoachId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtBookings_Users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Backfill legacy rows before adding the new constraints. Unknown sports retain class
            // capability; the four backlog sports receive their explicit approved capabilities.
            migrationBuilder.Sql("""
                UPDATE "Sports" SET "HasClasses" = TRUE, "Status" = 'Active';
                UPDATE "Sports" SET "HasClasses" = FALSE, "HasPt" = TRUE
                WHERE lower("Name") IN ('gym', 'boxing');
                UPDATE "Sports" SET "HasClasses" = TRUE, "HasPt" = FALSE
                WHERE lower("Name") IN ('yoga', 'groupx');
                UPDATE "Rooms" SET "Status" = CASE WHEN "IsActive" THEN 'Active' ELSE 'Inactive' END;
                UPDATE "Classes" c SET "RoomId" = (
                    SELECT cs."RoomId" FROM "ClassSessions" cs
                    WHERE cs."ClassId" = c."Id" ORDER BY cs."Date", cs."StartTime" LIMIT 1
                );
                """);

            // Abort with actionable messages instead of letting a later index/check fail opaquely.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "Sports" GROUP BY lower("Name") HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: duplicate sport names (case-insensitive)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Rooms" GROUP BY lower("Name") HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: duplicate room names (case-insensitive)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Rooms" WHERE "Capacity" <= 0) THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: room capacity must be positive';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Classes" WHERE "Capacity" <= 0 OR "EndDate" <= "StartDate") THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: invalid legacy class capacity/dates';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Classes" WHERE "RoomId" IS NULL) THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: a legacy class has no session room to backfill';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "ClassSessions" WHERE "EndTime" <= "StartTime") THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: invalid legacy session time range';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "ClassSessions"
                        GROUP BY "ClassId", "Date", "StartTime" HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Scheduling migration blocked: duplicate legacy class sessions';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "RoomId",
                table: "Classes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_Sports_Name_CI\" ON \"Sports\" (lower(\"Name\"));");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"UX_Rooms_Name_CI\" ON \"Rooms\" (lower(\"Name\"));");

            migrationBuilder.CreateIndex(
                name: "IX_Sports_Name",
                table: "Sports",
                column: "Name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sport_Capability",
                table: "Sports",
                sql: "\"HasClasses\" OR \"HasPt\"");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_Name",
                table: "Rooms",
                column: "Name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Room_Capacity",
                table: "Rooms",
                sql: "\"Capacity\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeduplicationKey",
                table: "Notifications",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemberSubscription_RemainingPt",
                table: "MemberSubscriptions",
                sql: "\"RemainingPtSessions\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPlans_SportId",
                table: "MembershipPlans",
                column: "SportId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MembershipPlan_PtCredits",
                table: "MembershipPlans",
                sql: "\"PtSessionCredits\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_CancelledByUserId",
                table: "ClassSessions",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_ClassId_Date_StartTime",
                table: "ClassSessions",
                columns: new[] { "ClassId", "Date", "StartTime" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClassSession_Times",
                table: "ClassSessions",
                sql: "\"EndTime\" > \"StartTime\"");

            migrationBuilder.CreateIndex(
                name: "IX_Classes_RoomId",
                table: "Classes",
                column: "RoomId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Class_Capacity",
                table: "Classes",
                sql: "\"Capacity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Class_Dates",
                table: "Classes",
                sql: "\"EndDate\" > \"StartDate\"");

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_PtBookingId_MemberId",
                table: "Attendances",
                columns: new[] { "PtBookingId", "MemberId" },
                unique: true,
                filter: "\"PtBookingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_SessionId_MemberId",
                table: "Attendances",
                columns: new[] { "SessionId", "MemberId" },
                unique: true,
                filter: "\"SessionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attendance_Target",
                table: "Attendances",
                sql: "(\"SessionId\" IS NULL) <> (\"PtBookingId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_CancelledByUserId",
                table: "ClassBookings",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_MemberId",
                table: "ClassBookings",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_PerformedByUserId",
                table: "ClassBookings",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_SessionId_MemberId",
                table: "ClassBookings",
                columns: new[] { "SessionId", "MemberId" },
                unique: true,
                filter: "\"Status\" = 'Confirmed'");

            migrationBuilder.CreateIndex(
                name: "IX_ClassBookings_SubscriptionId",
                table: "ClassBookings",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachAvailabilities_CoachId_DayOfWeek_StartTime",
                table: "CoachAvailabilities",
                columns: new[] { "CoachId", "DayOfWeek", "StartTime" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_CoachAvailabilities_SportId",
                table: "CoachAvailabilities",
                column: "SportId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_CancelledByUserId",
                table: "PtBookings",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_CoachId_Date_StartTime",
                table: "PtBookings",
                columns: new[] { "CoachId", "Date", "StartTime" },
                unique: true,
                filter: "\"Status\" = 'Confirmed'");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_MemberId",
                table: "PtBookings",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_PerformedByUserId",
                table: "PtBookings",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_RoomId",
                table: "PtBookings",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_SportId",
                table: "PtBookings",
                column: "SportId");

            migrationBuilder.CreateIndex(
                name: "IX_PtBookings_SubscriptionId",
                table: "PtBookings",
                column: "SubscriptionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attendances_PtBookings_PtBookingId",
                table: "Attendances",
                column: "PtBookingId",
                principalTable: "PtBookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Classes_Rooms_RoomId",
                table: "Classes",
                column: "RoomId",
                principalTable: "Rooms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClassSessions_Users_CancelledByUserId",
                table: "ClassSessions",
                column: "CancelledByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MembershipPlans_Sports_SportId",
                table: "MembershipPlans",
                column: "SportId",
                principalTable: "Sports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"UX_Sports_Name_CI\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"UX_Rooms_Name_CI\";");
            migrationBuilder.DropForeignKey(
                name: "FK_Attendances_PtBookings_PtBookingId",
                table: "Attendances");

            migrationBuilder.DropForeignKey(
                name: "FK_Classes_Rooms_RoomId",
                table: "Classes");

            migrationBuilder.DropForeignKey(
                name: "FK_ClassSessions_Users_CancelledByUserId",
                table: "ClassSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_MembershipPlans_Sports_SportId",
                table: "MembershipPlans");

            migrationBuilder.DropTable(
                name: "ClassBookings");

            migrationBuilder.DropTable(
                name: "CoachAvailabilities");

            migrationBuilder.DropTable(
                name: "PtBookings");

            migrationBuilder.DropIndex(
                name: "IX_Sports_Name",
                table: "Sports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sport_Capability",
                table: "Sports");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_Name",
                table: "Rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Room_Capacity",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_DeduplicationKey",
                table: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemberSubscription_RemainingPt",
                table: "MemberSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_MembershipPlans_SportId",
                table: "MembershipPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MembershipPlan_PtCredits",
                table: "MembershipPlans");

            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_CancelledByUserId",
                table: "ClassSessions");

            migrationBuilder.DropIndex(
                name: "IX_ClassSessions_ClassId_Date_StartTime",
                table: "ClassSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClassSession_Times",
                table: "ClassSessions");

            migrationBuilder.DropIndex(
                name: "IX_Classes_RoomId",
                table: "Classes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Class_Capacity",
                table: "Classes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Class_Dates",
                table: "Classes");

            migrationBuilder.DropIndex(
                name: "IX_Attendances_PtBookingId_MemberId",
                table: "Attendances");

            migrationBuilder.DropIndex(
                name: "IX_Attendances_SessionId_MemberId",
                table: "Attendances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attendance_Target",
                table: "Attendances");

            migrationBuilder.DropColumn(
                name: "HasClasses",
                table: "Sports");

            migrationBuilder.DropColumn(
                name: "HasPt",
                table: "Sports");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Sports");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RemainingPtSessions",
                table: "MemberSubscriptions");

            migrationBuilder.DropColumn(
                name: "IncludesClasses",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "PtSessionCredits",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "SportId",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "Classes");

            migrationBuilder.DropColumn(
                name: "PtBookingId",
                table: "Attendances");

            migrationBuilder.AlterColumn<int>(
                name: "CoachId",
                table: "Classes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SessionId",
                table: "Attendances",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassSessions_ClassId",
                table: "ClassSessions",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_SessionId_MemberId",
                table: "Attendances",
                columns: new[] { "SessionId", "MemberId" },
                unique: true);
        }
    }
}
