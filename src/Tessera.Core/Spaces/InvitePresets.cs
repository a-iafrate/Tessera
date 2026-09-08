namespace Tessera.Core.Spaces;

// Single source of truth for the three named presets InviteMember.razor offers (plus
// "Custom") — shared so SpaceDetail.razor can recognize "this member's permissions happen to
// match the Partner preset" without duplicating the level tuples a second time.
public static class InvitePresets
{
    public static readonly string[] Names = ["Partner", "Family", "Friend"];

    public static (AccessLevel ShoppingList, AccessLevel Expenses, AccessLevel Reminders, AccessLevel Calendar, AccessLevel Notes) Levels(string name) => name switch
    {
        "Partner" => (AccessLevel.Write, AccessLevel.Write, AccessLevel.Write, AccessLevel.Read, AccessLevel.Write),
        "Family" => (AccessLevel.Write, AccessLevel.None, AccessLevel.Write, AccessLevel.Availability, AccessLevel.Write),
        "Friend" => (AccessLevel.None, AccessLevel.None, AccessLevel.None, AccessLevel.Availability, AccessLevel.None),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a known preset name."),
    };

    // Null means the given combination doesn't correspond to any preset — a custom grant, or
    // one that started as a preset and was edited since (permissions have no memory of how
    // they were created).
    public static string? Match(AccessLevel shoppingList, AccessLevel expenses, AccessLevel reminders, AccessLevel calendar, AccessLevel notes)
    {
        foreach (var name in Names)
        {
            var levels = Levels(name);
            if (levels.ShoppingList == shoppingList && levels.Expenses == expenses && levels.Reminders == reminders
                && levels.Calendar == calendar && levels.Notes == notes)
            {
                return name;
            }
        }

        return null;
    }
}
