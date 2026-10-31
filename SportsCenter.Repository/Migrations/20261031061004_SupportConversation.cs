using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SportsCenter.Repository.Migrations
{
    /// <inheritdoc />
    public partial class SupportConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Subject",
                table: "SupportRequests",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "SupportRequests",
                type: "text",
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.AddColumn<int>(
                name: "CreatedById",
                table: "SupportRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityAt",
                table: "SupportRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql("UPDATE \"SupportRequests\" SET \"LastActivityAt\" = COALESCE(\"ResolvedAt\", \"CreatedAt\");");

            migrationBuilder.CreateTable(
                name: "SupportMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsInternal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportMessages_SupportRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "SupportRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupportMessages_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportRequests_CreatedById",
                table: "SupportRequests",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SupportRequests_Status_LastActivityAt",
                table: "SupportRequests",
                columns: new[] { "Status", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_AuthorId",
                table: "SupportMessages",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportMessages_RequestId",
                table: "SupportMessages",
                column: "RequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupportRequests_Users_CreatedById",
                table: "SupportRequests",
                column: "CreatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupportRequests_Users_CreatedById",
                table: "SupportRequests");

            migrationBuilder.DropTable(
                name: "SupportMessages");

            migrationBuilder.DropIndex(
                name: "IX_SupportRequests_CreatedById",
                table: "SupportRequests");

            migrationBuilder.DropIndex(
                name: "IX_SupportRequests_Status_LastActivityAt",
                table: "SupportRequests");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "SupportRequests");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "SupportRequests");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "SupportRequests");

            migrationBuilder.AlterColumn<string>(
                name: "Subject",
                table: "SupportRequests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(150)",
                oldMaxLength: 150);
        }
    }
}
