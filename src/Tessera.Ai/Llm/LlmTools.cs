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
    public const string ListShoppingLists = "list_shopping_lists";
    public const string RecordExpense = "record_expense";
    public const string QueryMonthlyExpenses = "query_monthly_expenses";
    public const string QueryExpenseHistory = "query_expense_history";
    public const string CreateReminder = "create_reminder";
    public const string CorrectLastShoppingItem = "correct_last_shopping_item";

    // Tools filtered per context (docs/05-ottimizzazioni.md) — the correction tool only makes
    // sense, and only gets included, when LlmContext.RecentAction is actually set.
    public static IReadOnlyList<ChatTool> Build(bool includeShoppingCorrection) =>
        includeShoppingCorrection ? [.. BaseTools, CorrectionTool] : BaseTools;

    private static readonly ChatTool CorrectionTool = ChatTool.CreateFunctionTool(
        CorrectLastShoppingItem,
        "Use ONLY when the user's message is a short correction to the item just added to the " +
        "shopping list mentioned in the context below (wrong quantity, wrong product, \"no I meant " +
        "X\") — never for adding a new, unrelated item.",
        BinaryData.FromString("""
            {
              "type": "object",
              "properties": { "corrected_text": { "type": "string", "description": "The corrected item text, replacing the one just added." } },
              "required": ["corrected_text"]
            }
            """));

    private static readonly IReadOnlyList<ChatTool> BaseTools =
    [
        ChatTool.CreateFunctionTool(
            AddShoppingItem,
            "Add an item to a shopping list. There's always a default list for plain grocery-type " +
            "items; use the list parameter only when the user names a specific, different list " +
            "(\"add sunscreen to the vacation list\") — naming one that doesn't exist yet creates it.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "The item text as the user wrote it, in their own language." },
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            CheckShoppingItem,
            "Mark an item on a shopping list as bought/done.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "The item text to match against the list." },
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            RemoveShoppingItem,
            "Remove an item from a shopping list entirely (not the same as checking it off).",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "The item text to match against the list." },
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  },
                  "required": ["item"]
                }
                """)),
        ChatTool.CreateFunctionTool(
            ShowShoppingList,
            "Show a shopping list's contents.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  }
                }
                """)),
        ChatTool.CreateFunctionTool(
            ClearShoppingList,
            "Remove every item from a shopping list.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  }
                }
                """)),
        ChatTool.CreateFunctionTool(
            ListShoppingLists,
            "Report which named shopping lists exist in this space (e.g. groceries, vacation, gifts).",
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
            QueryExpenseHistory,
            "Search past expenses and compute ONE aggregate value — never a list of individual " +
            "expenses. Use for questions like \"when did I last buy the drill\", \"how much do we " +
            "usually spend on gas\", \"how much did we spend last Christmas\". Work out date_from/" +
            "date_to from the phrase using the current date given in the context (e.g. \"last " +
            "Christmas\" is December of the previous year).",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "search_text": { "type": "string", "description": "Free text to match against the merchant or note of past expenses, e.g. \"drill\", \"gas\"." },
                    "category": { "type": "string", "description": "Category name, if the user named one." },
                    "date_from": { "type": "string", "description": "Start of the date range as an ISO date (YYYY-MM-DD), only if the question implies one." },
                    "date_to": { "type": "string", "description": "End of the date range as an ISO date (YYYY-MM-DD), only if the question implies one." },
                    "aggregation": {
                      "type": "string",
                      "enum": ["total", "average", "count", "most_recent_date"],
                      "description": "total: sum of matching amounts. average: mean of matching amounts. count: how many matching expenses. most_recent_date: the date of the latest matching expense — use this for \"when did I last...\"."
                    }
                  },
                  "required": ["aggregation"]
                }
                """)),
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
