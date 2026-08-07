using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Tessera.Ai.Llm;

// L3 of the router (docs/05-ottimizzazioni.md): reached only when L1/L2 found nothing. Function
// calling on Azure OpenAI, gpt-4o-mini. The system prompt and tool schema are static English
// text — translating them would fragment prompt caching across languages
// (docs/09-localizzazione.md) — and stay first in the request so the cacheable prefix never
// shifts. Anything that varies (language, time zone, current time, space name) goes in a
// second system message, never into the first.
public sealed class LlmFallbackClient(ChatClient chatClient, ILogger<LlmFallbackClient> logger)
{
    private const string SystemPrompt = """
        You are Tessera, a personal assistant for a household's shared shopping list, expenses
        and reminders, reached through a chat.

        Call exactly one tool when the user's message clearly maps to one of the actions
        available to you. If nothing fits, or the request is ambiguous, reply with a short
        plain-text message instead of calling a tool — never guess at an action you're not
        confident about.

        Always reply in the user's language, given in the context message below. Quote the
        user's own content (item names, merchants, reminder text) in the language they wrote
        it in — only the text you generate yourself follows their language. Keep replies short
        and conversational; this is a chat, not a document.

        For create_reminder, work out the due date and time from the message using the current
        date and time zone given in the context message. If the user gave no time of day, use
        09:00. Always resolve relative dates ("thursday", "in two weeks") into an absolute date.
        """;

    public async Task<LlmResult?> TryCompleteAsync(string userMessage, LlmContext context, CancellationToken ct)
    {
        try
        {
            List<ChatMessage> messages =
            [
                new SystemChatMessage(SystemPrompt),
                new SystemChatMessage(BuildContextMessage(context)),
                new UserChatMessage(userMessage),
            ];

            var options = new ChatCompletionOptions();
            foreach (var tool in LlmTools.All)
            {
                options.Tools.Add(tool);
            }

            var response = await chatClient.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            if (completion.ToolCalls.Count > 0)
            {
                var call = completion.ToolCalls[0];
                var arguments = JsonDocument.Parse(call.FunctionArguments).RootElement;
                return new LlmResult(ReplyText: null, new LlmToolCall(call.FunctionName, arguments));
            }

            var text = completion.Content.Count > 0 ? completion.Content[0].Text : null;
            return new LlmResult(text, ToolCall: null);
        }
        catch (Exception ex)
        {
            // The deterministic paths must survive an Azure OpenAI outage
            // (docs/06-roadmap.md: "fallback ai comandi deterministici quando l'LLM non è
            // disponibile") — the caller degrades to the honest "I didn't understand" reply.
            logger.LogError(ex, "L3 fallback call failed");
            return null;
        }
    }

    private static string BuildContextMessage(LlmContext context)
    {
        var localNow = TimeZoneInfo.ConvertTime(context.NowUtc, TimeZoneInfo.FindSystemTimeZoneById(context.TimeZoneId));
        return $"""
            Context:
            - User language: {context.Culture}
            - Time zone: {context.TimeZoneId}
            - Current date and time: {localNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}
            - Space: {context.SpaceName}
            """;
    }
}
