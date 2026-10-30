using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AiFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrainingPlans_MemberId",
                table: "TrainingPlans");

            migrationBuilder.DropIndex(
                name: "IX_AiChatMessages_UserId",
                table: "AiChatMessages");

            migrationBuilder.AlterColumn<int>(
                name: "CoachId",
                table: "TrainingPlans",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "CreatedByAi",
                table: "TrainingPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DaysPerWeek",
                table: "TrainingPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "TrainingPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "TrainingPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RepsText",
                table: "TrainingPlanItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionId",
                table: "TrainingPlanItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiDailyQuotas",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<string>(type: "text", nullable: false),
                    ChatCount = table.Column<int>(type: "integer", nullable: false),
                    GenerateCount = table.Column<int>(type: "integer", nullable: false),
                    AnalyzeCount = table.Column<int>(type: "integer", nullable: false),
                    EvaluateCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDailyQuotas", x => new { x.UserId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "AiFeatureUsageLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Feature = table.Column<string>(type: "text", nullable: false),
                    HasActiveSubscription = table.Column<bool>(type: "boolean", nullable: false),
                    ResponseSummary = table.Column<string>(type: "text", nullable: false),
                    RequestSnapshot = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFeatureUsageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiMonthlyBudgets",
                columns: table => new
                {
                    Period = table.Column<string>(type: "text", nullable: false),
                    SpentUsd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiMonthlyBudgets", x => x.Period);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Feature = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiWorkoutEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Report = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiWorkoutEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiWorkoutEvaluations_ClassSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClassSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiWorkoutEvaluations_Users_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrainingPlanSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanId = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<string>(type: "text", nullable: false),
                    Focus = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingPlanSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainingPlanSessions_TrainingPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "TrainingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingPlans_MemberId_IsActive",
                table: "TrainingPlans",
                columns: new[] { "MemberId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingPlanItems_SessionId",
                table: "TrainingPlanItems",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiChatMessages_UserId_CreatedAt",
                table: "AiChatMessages",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiFeatureUsageLogs_CreatedAt",
                table: "AiFeatureUsageLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_CreatedAt",
                table: "AiUsageLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiWorkoutEvaluations_MemberId_SessionId",
                table: "AiWorkoutEvaluations",
                columns: new[] { "MemberId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiWorkoutEvaluations_SessionId",
                table: "AiWorkoutEvaluations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingPlanSessions_PlanId",
                table: "TrainingPlanSessions",
                column: "PlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_TrainingPlanItems_TrainingPlanSessions_SessionId",
                table: "TrainingPlanItems",
                column: "SessionId",
                principalTable: "TrainingPlanSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TrainingPlanItems_TrainingPlanSessions_SessionId",
                table: "TrainingPlanItems");

            migrationBuilder.DropTable(
                name: "AiDailyQuotas");

            migrationBuilder.DropTable(
                name: "AiFeatureUsageLogs");

            migrationBuilder.DropTable(
                name: "AiMonthlyBudgets");

            migrationBuilder.DropTable(
                name: "AiUsageLogs");

            migrationBuilder.DropTable(
                name: "AiWorkoutEvaluations");

            migrationBuilder.DropTable(
                name: "TrainingPlanSessions");

            migrationBuilder.DropIndex(
                name: "IX_TrainingPlans_MemberId_IsActive",
                table: "TrainingPlans");

            migrationBuilder.DropIndex(
                name: "IX_TrainingPlanItems_SessionId",
                table: "TrainingPlanItems");

            migrationBuilder.DropIndex(
                name: "IX_AiChatMessages_UserId_CreatedAt",
                table: "AiChatMessages");

            migrationBuilder.DropColumn(
                name: "CreatedByAi",
                table: "TrainingPlans");

            migrationBuilder.DropColumn(
                name: "DaysPerWeek",
                table: "TrainingPlans");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "TrainingPlans");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "TrainingPlans");

            migrationBuilder.DropColumn(
                name: "RepsText",
                table: "TrainingPlanItems");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "TrainingPlanItems");

            migrationBuilder.AlterColumn<int>(
                name: "CoachId",
                table: "TrainingPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrainingPlans_MemberId",
                table: "TrainingPlans",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_AiChatMessages_UserId",
                table: "AiChatMessages",
                column: "UserId");
        }
    }
}
