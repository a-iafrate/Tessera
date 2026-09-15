using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnualBillingCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AnnualPrice",
                table: "SubscriptionPlans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PayPalPlanIdLiveAnnual",
                table: "SubscriptionPlans",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayPalPlanIdSandboxAnnual",
                table: "SubscriptionPlans",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillingCycle",
                table: "SpaceSubscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "AnnualPrice", "PayPalPlanIdLiveAnnual", "PayPalPlanIdSandboxAnnual" },
                values: new object[] { 0m, null, null });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "AnnualPrice", "PayPalPlanIdLiveAnnual", "PayPalPlanIdSandboxAnnual" },
                values: new object[] { 50m, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnualPrice",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "PayPalPlanIdLiveAnnual",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "PayPalPlanIdSandboxAnnual",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "BillingCycle",
                table: "SpaceSubscriptions");
        }
    }
}
