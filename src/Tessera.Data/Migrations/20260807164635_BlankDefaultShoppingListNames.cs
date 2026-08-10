using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tessera.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlankDefaultShoppingListNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Spesa" was only ever the C# default for a space's original list — never a name
            // a user could actually choose, since ShoppingListService had no way to name a
            // list until now. Blank rows render through a localized fallback instead
            // (Shopping.DefaultListName), so English users stop seeing an Italian literal.
            migrationBuilder.Sql("UPDATE [ShoppingLists] SET [Name] = N'' WHERE [Name] = N'Spesa';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [ShoppingLists] SET [Name] = N'Spesa' WHERE [Name] = N'';");
        }
    }
}
