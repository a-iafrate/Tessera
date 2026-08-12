using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifiedCalendarEventKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotifiedCalendarEvents_SpaceId_UserId_EventKey_EventStart",
                table: "NotifiedCalendarEvents");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "NotifiedCalendarEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_NotifiedCalendarEvents_SpaceId_UserId_EventKey_EventStart_Kind",
                table: "NotifiedCalendarEvents",
                columns: new[] { "SpaceId", "UserId", "EventKey", "EventStart", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotifiedCalendarEvents_SpaceId_UserId_EventKey_EventStart_Kind",
                table: "NotifiedCalendarEvents");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "NotifiedCalendarEvents");

            migrationBuilder.CreateIndex(
                name: "IX_NotifiedCalendarEvents_SpaceId_UserId_EventKey_EventStart",
                table: "NotifiedCalendarEvents",
                columns: new[] { "SpaceId", "UserId", "EventKey", "EventStart" },
                unique: true);
        }
    }
}
