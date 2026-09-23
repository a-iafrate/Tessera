namespace Tessera.Core.Help;

// snake_case wire format for HelpTopic, matching the enum convention every other tool schema in
// LlmTools.cs already uses (e.g. QueryExpenseHistory's "aggregation": "most_recent_date"). Kept
// as an explicit map rather than Enum.TryParse(text, ignoreCase: true, ...) on the C# names
// directly — that pattern silently fails on multi-word values (PascalCase vs. snake_case is not
// a case difference), which would make an unrecognized topic degrade invisibly instead of
// falling through to the caller's own "topic not understood" handling.
public static class HelpTopicCodes
{
    public static string ToCode(HelpTopic topic) => topic switch
    {
        HelpTopic.ShoppingList => "shopping_list",
        HelpTopic.Expenses => "expenses",
        HelpTopic.Reminders => "reminders",
        HelpTopic.Notes => "notes",
        HelpTopic.Calendar => "calendar",
        HelpTopic.Sharing => "sharing",
        HelpTopic.Digest => "digest",
        HelpTopic.Voice => "voice",
        HelpTopic.Undo => "undo",
        _ => throw new ArgumentOutOfRangeException(nameof(topic)),
    };

    public static HelpTopic? FromCode(string? code) => code switch
    {
        "shopping_list" => HelpTopic.ShoppingList,
        "expenses" => HelpTopic.Expenses,
        "reminders" => HelpTopic.Reminders,
        "notes" => HelpTopic.Notes,
        "calendar" => HelpTopic.Calendar,
        "sharing" => HelpTopic.Sharing,
        "digest" => HelpTopic.Digest,
        "voice" => HelpTopic.Voice,
        "undo" => HelpTopic.Undo,
        _ => null,
    };
}
