using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Recurrence_Frequency = table.Column<int>(type: "int", nullable: false),
                    Recurrence_Interval = table.Column<int>(type: "int", nullable: false),
                    Recurrence_DaysOfWeek = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Recurrence_DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AutoRegister = table.Column<bool>(type: "bit", nullable: false),
                    LastGeneratedFor = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_SpaceId_LastGeneratedFor",
                table: "RecurringExpenses",
                columns: new[] { "SpaceId", "LastGeneratedFor" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringExpenses");
        }
    }
}
