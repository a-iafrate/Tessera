using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestHourLocal",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastDigestSentFor",
                table: "Users",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestHourLocal",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastDigestSentFor",
                table: "Users");
        }
    }
}
