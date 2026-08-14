using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Tessera.Ai.Llm;

// A distinct LLM surface from LlmFallbackClient, same reasoning as ReceiptVisionClient: no
// function calling, no general tool set, single purpose. Text-only (docs/06-roadmap.md Fase 4:
// "buon caso d'uso LLM, costo contenuto") — no schema to parse, the model's prose is the
// entire result, so a plain completion is all this needs.
public sealed class RecipeSuggestionClient(ChatClient chatClient, ILogger<RecipeSuggestionClient> logger)
{
    private const string SystemPrompt = """
        You suggest simple recipes using some of the items on a household's shopping list.
        Given the items and, optionally, a preference the user stated (a cuisine, a dietary
        restriction, "something quick"), suggest 2-3 short recipe ideas that use some of those
        items — not necessarily all of them, and you may assume basic pantry staples (salt,
        oil, water, ...) are available even if not listed. If the items don't suggest anything
        sensible to cook, say so briefly instead of forcing an answer.

        Reply in the language given in the context message, as plain text: one short line per
        recipe (its name, then a brief one-sentence description), never a numbered or bulleted
        list, no headings, no markdown. Never mention how you work or that you were given a
        list of items.
        """;

    public async Task<string?> SuggestAsync(IReadOnlyList<string> items, string? preference, string culture, CancellationToken ct)
    {
        try
        {
            var userText = preference is { Length: > 0 }
                ? $"Items: {string.Join(", ", items)}\nPreference: {preference}"
                : $"Items: {string.Join(", ", items)}";

            List<ChatMessage> messages =
            [
                new SystemChatMessage(SystemPrompt),
                new SystemChatMessage($"Reply in this language: {culture}."),
                new UserChatMessage(userText),
            ];

            var response = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
            var completion = response.Value;
            return completion.Content.Count > 0 ? completion.Content[0].Text : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recipe suggestion call failed");
            return null;
        }
    }
}
