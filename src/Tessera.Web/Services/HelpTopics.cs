using Microsoft.Extensions.Localization;
using Tessera.Core.Help;
using Tessera.Core.Resources;

namespace Tessera.Web.Services;

// Shared between the bot's get_help tool (MessageProcessor.HandleGetHelpAsync) and the web
// console's /help page (Help.razor) — one place for the question/answer text and which page (if
// any) answers it, so the two surfaces can't describe the same feature differently.
public static class HelpTopics
{
    public static IReadOnlyList<HelpTopic> All { get; } = Enum.GetValues<HelpTopic>();

    public static string GetQuestion(HelpTopic topic, IStringLocalizer<Messages> localizer) => localizer[$"Help.{topic}.Question"];

    public static string GetAnswer(HelpTopic topic, IStringLocalizer<Messages> localizer) => localizer[$"Help.{topic}.Answer"];

    // The route pattern for the page that answers this topic, with "{0}" standing in for a
    // space id — null for topics answered entirely in chat, with nothing to link to (undo,
    // voice, digest: all pure conversation, no web-console counterpart).
    public static string? GetRoutePattern(HelpTopic topic) => topic switch
    {
        HelpTopic.ShoppingList => "/spaces/{0}/shopping-list",
        HelpTopic.Expenses => "/spaces/{0}/expenses",
        HelpTopic.Reminders => "/spaces/{0}/reminders",
        HelpTopic.Notes => "/spaces/{0}/notes",
        HelpTopic.Calendar => "/spaces/{0}/calendars",
        HelpTopic.Sharing => "/spaces/{0}/invite",
        _ => null,
    };
}
