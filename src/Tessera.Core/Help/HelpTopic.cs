namespace Tessera.Core.Help;

// The shared vocabulary between the bot's get_help LLM tool (Tessera.Ai.Llm.LlmTools) and the
// web console's /help page (Tessera.Web.Services.HelpTopics) — one enum, not two separate lists
// that could drift. No ResourceKind of its own: help touches no resource, same reasoning as
// /language and /help already having no space resolution requirement.
public enum HelpTopic
{
    ShoppingList,
    Expenses,
    Reminders,
    Notes,
    Calendar,
    Sharing,
    Digest,
    Voice,
    Undo,
}
