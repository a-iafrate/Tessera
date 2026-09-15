using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReshapeSubscriptionPlansToValueAxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_SpaceId_OccurredAt",
                table: "UsageEvents");

            // Old Plus (...003) and Family (...004) are merged into the new Plus (...002) — any
            // space still on one of the retired ids must move first, since Space.PlanId -> Id is
            // Restrict and would otherwise block the DeleteData calls below.
            migrationBuilder.Sql(
                "UPDATE Spaces SET PlanId = '10000000-0000-0000-0000-000000000002' " +
                "WHERE PlanId IN ('10000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000004')");

            migrationBuilder.DeleteData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"));

            migrationBuilder.RenameColumn(
                name: "AllowsReceiptScanning",
                table: "SubscriptionPlans",
                newName: "AllowsExport");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "UsageEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HistoryMonths",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxLinkedCalendars",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxReceiptsPerMonth",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxSpacesOwned",
                table: "SubscriptionPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "HistoryMonths", "MaxLinkedBots", "MaxLinkedCalendars", "MaxReceiptsPerMonth", "MaxSpacesOwned" },
                values: new object[] { 3, 999, 1, 3, 1 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "HistoryMonths", "MaxCallsPerDay", "MaxLinkedBots", "MaxLinkedCalendars", "MaxReceiptsPerMonth", "MaxSpacesOwned", "Name" },
                values: new object[] { 0, 1000, 999, 999, 999, 999, "Plus" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_SpaceId_Kind_OccurredAt",
                table: "UsageEvents",
                columns: new[] { "SpaceId", "Kind", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: spaces moved off the retired Plus/Family ids by the Sql() call in Up() stay on
            // the merged Plus id — which of the two retired plans they originally had is not recoverable.
            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_SpaceId_Kind_OccurredAt",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "HistoryMonths",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "MaxLinkedCalendars",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "MaxReceiptsPerMonth",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "MaxSpacesOwned",
                table: "SubscriptionPlans");

            migrationBuilder.RenameColumn(
                name: "AllowsExport",
                table: "SubscriptionPlans",
                newName: "AllowsReceiptScanning");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "MaxLinkedBots",
                value: 1);

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "MaxCallsPerDay", "MaxLinkedBots", "Name" },
                values: new object[] { 200, 1, "Basic" });

            migrationBuilder.InsertData(
                table: "SubscriptionPlans",
                columns: new[] { "Id", "AllowsReceiptScanning", "Currency", "MaxCallsPerDay", "MaxLinkedBots", "MonthlyPrice", "Name", "PayPalPlanIdLive", "PayPalPlanIdSandbox" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000003"), true, "EUR", 1000, 3, 12m, "Plus", null, null },
                    { new Guid("10000000-0000-0000-0000-000000000004"), true, "EUR", 5000, 10, 25m, "Family", null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_SpaceId_OccurredAt",
                table: "UsageEvents",
                columns: new[] { "SpaceId", "OccurredAt" });
        }
    }
}
