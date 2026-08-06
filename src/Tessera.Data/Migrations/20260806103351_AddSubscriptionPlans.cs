using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "Spaces",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("10000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateTable(
                name: "SubscriptionPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaxLinkedBots = table.Column<int>(type: "int", nullable: false),
                    MaxCallsPerDay = table.Column<int>(type: "int", nullable: false),
                    MonthlyPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlans", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SubscriptionPlans",
                columns: new[] { "Id", "Currency", "MaxCallsPerDay", "MaxLinkedBots", "MonthlyPrice", "Name" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "EUR", 20, 1, 0m, "Free" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "EUR", 200, 1, 5m, "Basic" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "EUR", 1000, 3, 12m, "Plus" },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "EUR", 5000, 10, 25m, "Family" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_PlanId",
                table: "Spaces",
                column: "PlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_Spaces_SubscriptionPlans_PlanId",
                table: "Spaces",
                column: "PlanId",
                principalTable: "SubscriptionPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Spaces_SubscriptionPlans_PlanId",
                table: "Spaces");

            migrationBuilder.DropTable(
                name: "SubscriptionPlans");

            migrationBuilder.DropIndex(
                name: "IX_Spaces_PlanId",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "Spaces");
        }
    }
}
