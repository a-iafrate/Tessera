using OpenAI.Chat;
using Tessera.Core.Spaces;

namespace Tessera.Ai.Llm;

// The tool schema is part of the cacheable prefix (docs/05-ottimizzazioni.md) — for a given
// space and a given member's permissions it must stay byte-for-byte identical across turns.
// Every description is in English regardless of the user's language (docs/09-localizzazione.md):
// the model reads these, the user never does.
//
// Build filters this fixed list down to what the calling member can actually use
// (docs/05-ottimizzazioni.md, "Schema dei tool per contesto"): a space with no calendar linked
// never sees the five calendar tools, and a member with only Read on Expenses never sees
// record_expense. AllTools stays a single ordered list, not a dictionary, so the filtered
// output's order — and therefore its serialized bytes — is deterministic within a running
// instance regardless of which subset survives the filter.
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
    public const string QueryPriceHistory = "query_price_history";
    public const string CreateReminder = "create_reminder";
    public const string CreateNote = "create_note";
    public const string ShowNotes = "show_notes";
    public const string DeleteNote = "delete_note";
    public const string QueryCalendarEvents = "query_calendar_events";
    public const string QueryCalendarFreeBusy = "query_calendar_freebusy";
    public const string CreateCalendarEvent = "create_calendar_event";
    public const string DeleteCalendarEvent = "delete_calendar_event";
    public const string MoveCalendarEvent = "move_calendar_event";
    public const string CorrectLastShoppingItem = "correct_last_shopping_item";
    public const string SuggestRecipes = "suggest_recipes";

    // Tools filtered per context (docs/05-ottimizzazioni.md):
    // - accessByResource: the calling member's effective AccessLevel per ResourceKind in the
    //   current space (Admin for the space owner, AccessLevel.None for a resource with no
    //   MembershipPermission row at all) — a tool is offered only when that level meets or
    //   exceeds the ToolSpec's Required.
    // - hasLinkedCalendar: whether the space has at least one CalendarSpaceMapping — without
    //   one the five calendar tools are dead weight even for a member with full Calendar
    //   permission, since there's nothing for them to act on.
    // - includeShoppingCorrection: the correction tool only makes sense, and only gets
    //   included, when LlmContext.RecentAction is actually set.
    public static IReadOnlyList<ChatTool> Build(
        IReadOnlyDictionary<ResourceKind, AccessLevel> accessByResource,
        bool hasLinkedCalendar,
        bool includeShoppingCorrection)
    {
        var tools = new List<ChatTool>(AllTools.Count);
        foreach (var spec in AllTools)
        {
            if (spec.Name == CorrectLastShoppingItem && !includeShoppingCorrection)
            {
                continue;
            }

            if (spec.Resource == ResourceKind.Calendar && !hasLinkedCalendar)
            {
                continue;
            }

            var level = accessByResource.GetValueOrDefault(spec.Resource, AccessLevel.None);
            if (level < spec.Required)
            {
                continue;
            }

            tools.Add(spec.Tool);
        }

        return tools;
    }

    private readonly record struct ToolSpec(string Name, ResourceKind Resource, AccessLevel Required, ChatTool Tool);

    private static readonly IReadOnlyList<ToolSpec> AllTools =
    [
        new(AddShoppingItem, ResourceKind.ShoppingList, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
        new(CheckShoppingItem, ResourceKind.ShoppingList, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
        new(RemoveShoppingItem, ResourceKind.ShoppingList, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
        new(ShowShoppingList, ResourceKind.ShoppingList, AccessLevel.Read, ChatTool.CreateFunctionTool(
            ShowShoppingList,
            "Show a shopping list's contents.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  }
                }
                """))),
        new(ClearShoppingList, ResourceKind.ShoppingList, AccessLevel.Write, ChatTool.CreateFunctionTool(
            ClearShoppingList,
            "Remove every item from a shopping list.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Name of a specific list, only if the user named one other than the default." }
                  }
                }
                """))),
        new(ListShoppingLists, ResourceKind.ShoppingList, AccessLevel.Read, ChatTool.CreateFunctionTool(
            ListShoppingLists,
            "Report which named shopping lists exist in this space (e.g. groceries, vacation, gifts).",
            BinaryData.FromString("""{ "type": "object", "properties": {} }"""))),
        new(RecordExpense, ResourceKind.Expenses, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
        new(QueryMonthlyExpenses, ResourceKind.Expenses, AccessLevel.Read, ChatTool.CreateFunctionTool(
            QueryMonthlyExpenses,
            "Report how much has been spent this month.",
            BinaryData.FromString("""{ "type": "object", "properties": {} }"""))),
        new(QueryExpenseHistory, ResourceKind.Expenses, AccessLevel.Read, ChatTool.CreateFunctionTool(
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
                """))),
        new(QueryPriceHistory, ResourceKind.Expenses, AccessLevel.Read, ChatTool.CreateFunctionTool(
            QueryPriceHistory,
            "Look up how a product's price has changed over time, from products previously " +
            "extracted from scanned receipts. Use for questions like \"does coffee cost more " +
            "than it used to\", \"has the price of milk gone up\", \"how much more expensive is " +
            "olive oil than 6 months ago\".",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "product": { "type": "string", "description": "The product name to look up, e.g. \"coffee\", \"olive oil\"." },
                    "compare_to_date": { "type": "string", "description": "An ISO date (YYYY-MM-DD) to compare the current price against, worked out from the current date given in the context (e.g. \"6 months ago\"). Omit to compare against the earliest price on file." }
                  },
                  "required": ["product"]
                }
                """))),
        new(SuggestRecipes, ResourceKind.ShoppingList, AccessLevel.Read, ChatTool.CreateFunctionTool(
            SuggestRecipes,
            "Suggest recipes using the items currently on the shopping list. Use for questions " +
            "like \"what can I cook with what's on the list\", \"give me a dinner idea\", " +
            "\"suggest a quick vegetarian recipe from my list\".",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "preference": { "type": "string", "description": "A cuisine, dietary restriction, or constraint the user mentioned (e.g. \"vegetarian\", \"quick\", \"italian\"), if any." }
                  }
                }
                """))),
        new(CreateReminder, ResourceKind.Reminders, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
        new(CreateNote, ResourceKind.Notes, AccessLevel.Write, ChatTool.CreateFunctionTool(
            CreateNote,
            "Save a free-text note shared with the space — a shopping list item, reminder, or " +
            "expense (something structured), never use this instead.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "description": "A short title, only if the phrasing clearly implies one (e.g. \"note: wifi password, it's 1234\" has title \"wifi password\")." },
                    "body": { "type": "string", "description": "The note's content, in the user's own words." }
                  },
                  "required": ["body"]
                }
                """))),
        new(ShowNotes, ResourceKind.Notes, AccessLevel.Read, ChatTool.CreateFunctionTool(
            ShowNotes,
            "List the notes saved in this space.",
            BinaryData.FromString("""{ "type": "object", "properties": {} }"""))),
        new(DeleteNote, ResourceKind.Notes, AccessLevel.Write, ChatTool.CreateFunctionTool(
            DeleteNote,
            "Delete a note.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "search_text": { "type": "string", "description": "Free text to match against the note's title or body." }
                  },
                  "required": ["search_text"]
                }
                """))),
        new(QueryCalendarEvents, ResourceKind.Calendar, AccessLevel.Read, ChatTool.CreateFunctionTool(
            QueryCalendarEvents,
            "List calendar events (with titles) in a date/time range — use for \"what do I have " +
            "tomorrow\", \"what's on the calendar this week\". Not for availability-only questions.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "from": { "type": "string", "description": "Start of the range as an ISO 8601 date-time, worked out from the current date and time zone given in the context." },
                    "to": { "type": "string", "description": "End of the range as an ISO 8601 date-time." }
                  },
                  "required": ["from", "to"]
                }
                """))),
        new(QueryCalendarFreeBusy, ResourceKind.Calendar, AccessLevel.Availability, ChatTool.CreateFunctionTool(
            QueryCalendarFreeBusy,
            "Check when the space is free or busy in a date/time range, without event titles — " +
            "use for \"when are we free\", \"am I busy tomorrow afternoon\", \"when are Sara and I " +
            "both free Thursday\".",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "from": { "type": "string", "description": "Start of the range as an ISO 8601 date-time, worked out from the current date and time zone given in the context." },
                    "to": { "type": "string", "description": "End of the range as an ISO 8601 date-time." },
                    "people": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Names of specific space members the user asked about, e.g. \"me and Sara\" -> [\"Sara\"] (the asker themselves is always included automatically, never list them here). Leave empty/omit for a whole-space \"is anyone busy\" question."
                    }
                  },
                  "required": ["from", "to"]
                }
                """))),
        new(CreateCalendarEvent, ResourceKind.Calendar, AccessLevel.Write, ChatTool.CreateFunctionTool(
            CreateCalendarEvent,
            "Create a calendar event with a specific start and end time — use for \"add dentist " +
            "appointment tomorrow at 5pm\". Never use this for a plain reminder, which has no duration.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "description": "The event title, in the user's own words." },
                    "start": { "type": "string", "description": "Start date-time as ISO 8601, worked out from the current date and time zone given in the context." },
                    "end": { "type": "string", "description": "End date-time as ISO 8601. If the user gave no duration, default to one hour after start." }
                  },
                  "required": ["title", "start", "end"]
                }
                """))),
        new(DeleteCalendarEvent, ResourceKind.Calendar, AccessLevel.Write, ChatTool.CreateFunctionTool(
            DeleteCalendarEvent,
            "Delete a calendar event — use for \"cancel the dentist appointment\", \"remove the " +
            "meeting with Marco from the calendar\". Always work out a search window even if the " +
            "user gave no explicit date: default from the current date/time given in the context " +
            "to 30 days after it.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "search_text": { "type": "string", "description": "Free text to match against the event's title." },
                    "from": { "type": "string", "description": "Start of the window to search within, as ISO 8601 date-time, worked out from the current date and time zone given in the context." },
                    "to": { "type": "string", "description": "End of the window to search within, as ISO 8601 date-time." }
                  },
                  "required": ["search_text", "from", "to"]
                }
                """))),
        new(MoveCalendarEvent, ResourceKind.Calendar, AccessLevel.Write, ChatTool.CreateFunctionTool(
            MoveCalendarEvent,
            "Move/reschedule an existing calendar event to a new date/time, keeping its original " +
            "duration — use for \"move the dentist appointment to 5pm\", \"reschedule the meeting " +
            "with Marco to Friday\". Always work out a search window even if the user gave no " +
            "explicit date: default from the current date/time given in the context to 30 days " +
            "after it.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "search_text": { "type": "string", "description": "Free text to match against the event's title." },
                    "from": { "type": "string", "description": "Start of the window to search within for the existing event, as ISO 8601 date-time, worked out from the current date and time zone given in the context." },
                    "to": { "type": "string", "description": "End of the window to search within for the existing event, as ISO 8601 date-time." },
                    "new_start": { "type": "string", "description": "The new start date-time as ISO 8601, worked out from the current date and time zone given in the context. The event keeps its original duration — do not compute a new end time." }
                  },
                  "required": ["search_text", "from", "to", "new_start"]
                }
                """))),
        // Kept last so it's appended after every base tool that qualifies, matching the
        // original [.. BaseTools, CorrectionTool] ordering — its own Resource/Required entry
        // mirrors AddShoppingItem's, since a correction is itself a shopping-list write.
        new(CorrectLastShoppingItem, ResourceKind.ShoppingList, AccessLevel.Write, ChatTool.CreateFunctionTool(
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
                """))),
    ];
}
