using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarLinkingEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarSpaceMappings",
                columns: table => new
                {
                    ExternalCalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShareLevel = table.Column<int>(type: "int", nullable: false),
                    IsDefaultWriteTarget = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSpaceMappings", x => new { x.ExternalCalendarId, x.SpaceId });
                });

            migrationBuilder.CreateTable(
                name: "ExternalCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderCalendarId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    ProviderRole = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalCalendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkedAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProviderEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenSecretName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkedAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendars_LinkedAccountId_ProviderCalendarId",
                table: "ExternalCalendars",
                columns: new[] { "LinkedAccountId", "ProviderCalendarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAccounts_UserId_Provider",
                table: "LinkedAccounts",
                columns: new[] { "UserId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarSpaceMappings");

            migrationBuilder.DropTable(
                name: "ExternalCalendars");

            migrationBuilder.DropTable(
                name: "LinkedAccounts");
        }
    }
}
