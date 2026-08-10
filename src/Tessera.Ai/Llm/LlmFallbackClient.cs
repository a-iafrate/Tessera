using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Tessera.Ai.Llm;

// L3 of the router (docs/05-ottimizzazioni.md): reached only when L1/L2 found nothing. Function
// calling on Azure OpenAI, gpt-4o-mini. The system prompt and tool schema are static English
// text — translating them would fragment prompt caching across languages
// (docs/09-localizzazione.md) — and stay first in the request so the cacheable prefix never
// shifts. Anything that varies (language, time zone, current time, space name) goes in a
// second system message, never into the first.
public sealed class LlmFallbackClient(ChatClient chatClient, ILogger<LlmFallbackClient> logger, TelemetryClient? telemetry = null)
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
        it in — only the text you generate yourself follows their language.

        Tone, when you reply with plain text instead of calling a tool:
        - One or two lines, never a document. No headings, no bullet lists, no bold/italic markup.
        - If something went wrong, a brief apology once — never repeat it, and don't over-explain.
        - Emoji only if they add real meaning (a checkmark for a confirmation, a clock for a
          reminder) — never decorative, never more than one per message.
        - Never mention how you work: don't say "fast path", "the model", "intent", the name of
          a tool, or anything about how the request was processed.
        - Don't end with a courtesy question like "anything else?" — this chat stays open, that
          question every turn is just noise.

        For create_reminder, work out the due date and time from the message using the current
        date and time zone given in the context message. If the user gave no time of day, use
        09:00. Always resolve relative dates ("thursday", "in two weeks") into an absolute date.

        If the context message below mentions a recent action, and the current message is a
        short correction to it rather than a new, unrelated request, use the matching
        correction tool instead of the normal add/create tool.
        """;

    public async Task<LlmResult?> TryCompleteAsync(string userMessage, LlmContext context, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            List<ChatMessage> messages =
            [
                new SystemChatMessage(SystemPrompt),
                new SystemChatMessage(BuildContextMessage(context)),
                new UserChatMessage(userMessage),
            ];

            var options = new ChatCompletionOptions();
            foreach (var tool in LlmTools.Build(includeShoppingCorrection: context.RecentAction is not null))
            {
                options.Tools.Add(tool);
            }

            var response = await chatClient.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            TrackTurn(context, stopwatch.Elapsed, completion.Usage);

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

    // Token cost and latency per turn (docs/05-ottimizzazioni.md: "Token consumati per turno,
    // p50 e p95" / "Latenza end-to-end per livello di router") — TrackMetric records each raw
    // value, Application Insights computes the percentiles afterward.
    private void TrackTurn(LlmContext context, TimeSpan elapsed, ChatTokenUsage? usage)
    {
        telemetry?.TrackMetric(new MetricTelemetry("L3LatencyMs", elapsed.TotalMilliseconds) { Properties = { ["Culture"] = context.Culture } });

        if (usage is not null)
        {
            telemetry?.TrackMetric(new MetricTelemetry("L3TokensTotal", usage.TotalTokenCount) { Properties = { ["Culture"] = context.Culture } });
        }
    }

    private static string BuildContextMessage(LlmContext context)
    {
        var localNow = TimeZoneInfo.ConvertTime(context.NowUtc, TimeZoneInfo.FindSystemTimeZoneById(context.TimeZoneId));
        var lines = new List<string>
        {
            "Context:",
            $"- User language: {context.Culture}",
            $"- Time zone: {context.TimeZoneId}",
            $"- Current date and time: {localNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}",
            $"- Space: {context.SpaceName}",
        };

        if (context.RecentAction is not null)
        {
            lines.Add($"- Recent action, a moment ago: {context.RecentAction}");
        }

        return string.Join('\n', lines);
    }
}
