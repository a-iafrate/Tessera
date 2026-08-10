using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingAndUndo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SharingPromptShown",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UsefulActionCount",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LastOperations",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UndoPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PerformedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsUndone = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LastOperations", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingHints",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HintKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ShownCount = table.Column<int>(type: "int", nullable: false),
                    Dismissed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingHints", x => new { x.UserId, x.HintKey });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LastOperations");

            migrationBuilder.DropTable(
                name: "OnboardingHints");

            migrationBuilder.DropColumn(
                name: "SharingPromptShown",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UsefulActionCount",
                table: "Users");
        }
    }
}
