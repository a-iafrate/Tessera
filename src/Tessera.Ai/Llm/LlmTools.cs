using OpenAI.Chat;

namespace Tessera.Ai.Llm;

// The tool schema is part of the cacheable prefix (docs/05-ottimizzazioni.md) — it must stay
// byte-for-byte identical across turns, so it's built once as a static list, never per-request.
// Every description is in English regardless of the user's language (docs/09-localizzazione.md):
// the model reads these, the user never does.
public static class LlmTools
{
    public const string AddShoppingItem = "add_shopping_item";
    public const string CheckShoppingItem = "check_shopping_item";
    public const string RemoveShoppingItem = "remove_shopping_item";
    public const string ShowShoppingList = "show_shopping_list";
    public const string ClearShoppingList = "clear_shopping_list";
    public const string RecordExpense = "record_expense";
    public const string QueryMonthlyExpenses = "query_monthly_expenses";
    public const string CreateReminder = "create_reminder";

    public static readonly IReadOnlyList<ChatTool> All =
    [
        ChatTool.CreateFunctionTool(
            AddShoppingItem,
            "Add an item to the shared shopping list.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": { "item": { "type": "string", "description": "The item text as the user wrote it, in their own language." } },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            CheckShoppingItem,
            "Mark an item on the shopping list as bought/done.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": { "item": { "type": "string", "description": "The item text to match against the list." } },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            RemoveShoppingItem,
            "Remove an item from the shopping list entirely (not the same as checking it off).",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": { "item": { "type": "string", "description": "The item text to match against the list." } },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            ShowShoppingList,
            "Show the current shopping list.",
            BinaryData.FromString("""{ "type": "object", "properties": {} }""")),
        ChatTool.CreateFunctionTool(
            ClearShoppingList,
            "Remove every item from the shopping list.",
            BinaryData.FromString("""{ "type": "object", "properties": {} }""")),
        ChatTool.CreateFunctionTool(
            RecordExpense,
            "Record a new expense.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "amount": { "type": "number", "description": "The amount spent, as a plain number." },
                    "category": { "type": "string", "description": "Free-text category if the user mentioned one, e.g. groceries, transport." },
                    "merchant": { "type": "string", "description": "Where the money was spent, if the user mentioned it." }
                  },
                  "required": ["amount"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            QueryMonthlyExpenses,
            "Report how much has been spent this month.",
            BinaryData.FromString("""{ "type": "object", "properties": {} }""")),
        ChatTool.CreateFunctionTool(
            CreateReminder,
            "Create a one-time reminder at a specific date and time.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "text": { "type": "string", "description": "What to be reminded of, in the user's own words." },
                    "due_at": { "type": "string", "description": "The date and time the reminder is due, as an ISO 8601 date-time (e.g. 2026-08-13T09:00:00), worked out from the current date and time zone given in the context." }
                  },
                  "required": ["text", "due_at"]
                }
                """)),
    ];
}
