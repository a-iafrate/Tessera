using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Tessera.Ai.Llm;

// Price is nullable because not every receipt prints a clean per-line price the model can
// separate from quantity/discount markup — when it can't, the item still checks off the
// shopping list and lands in Expense.Note, it just doesn't feed price history
// (docs/06-roadmap.md "Storico prezzi").
public sealed record ReceiptItem(string Name, decimal? Price);

public sealed record ReceiptExtraction(string? Merchant, decimal Total, IReadOnlyList<ReceiptItem> Items);

// A distinct LLM surface from LlmFallbackClient — not part of the L1/L2/L3 router, no
// conversational tone, no general tool set, single purpose. Real per-call cost
// (docs/06-roadmap.md Fase 4: "costo reale ma accettabile, centesimi per scontrino") — the
// image is sent inline as bytes, never uploaded to blob storage first, since the extraction
// itself doesn't need a durable URL (only the resulting Expense's attachment does, handled
// separately by MessageProcessor after the amount is known).
public sealed class ReceiptVisionClient(ChatClient chatClient, ILogger<ReceiptVisionClient> logger)
{
    private const string ExtractReceiptTool = "extract_receipt";

    private const string SystemPrompt = """
        You read a photo of a shopping receipt. Extract the merchant name (if legible), the
        total amount actually paid, and the individual products bought. For each product, give
        an ordinary shopping-list-style name (e.g. "milk", "bread") rather than the abbreviated
        or coded text a receipt often prints — normalize it the way a person would say it out
        loud, in whatever language the receipt is in. Also give the price actually paid for
        that line, if you can read it clearly; omit it rather than guessing. Skip lines that
        aren't products (bag fees, loyalty discounts, subtotals). Call extract_receipt exactly
        once with what you can read. If the image isn't a legible receipt, don't call the tool
        — reply with a short plain-text message saying so instead.
        """;

    private static readonly ChatTool Tool = ChatTool.CreateFunctionTool(
        ExtractReceiptTool,
        "Report the merchant, total amount, and purchased products read from a receipt photo.",
        BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "merchant": { "type": "string", "description": "The merchant/store name as printed, or omitted if illegible." },
                "total": { "type": "number", "description": "The total amount actually paid, as a plain decimal number." },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string", "description": "Ordinary shopping-list-style name of the product, in the receipt's own language." },
                      "price": { "type": "number", "description": "The price actually paid for this line, as a plain decimal number. Omit if not clearly legible." }
                    },
                    "required": ["name"]
                  },
                  "description": "One entry per purchased product line item. Omit if none are legible."
                }
              },
              "required": ["total"]
            }
            """));

    public async Task<ReceiptExtraction?> ExtractAsync(BinaryData imageBytes, string mimeType, CancellationToken ct)
    {
        try
        {
            List<ChatMessage> messages =
            [
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(ChatMessageContentPart.CreateImagePart(imageBytes, mimeType)),
            ];

            var options = new ChatCompletionOptions();
            options.Tools.Add(Tool);

            var response = await chatClient.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;
            if (completion.ToolCalls.Count == 0)
            {
                return null;
            }

            var args = JsonDocument.Parse(completion.ToolCalls[0].FunctionArguments).RootElement;
            var merchant = args.TryGetProperty("merchant", out var merchantProp) ? merchantProp.GetString() : null;
            var items = args.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array
                ? itemsProp.EnumerateArray()
                    .Where(x => x.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
                    .Select(x => new ReceiptItem(
                        x.GetProperty("name").GetString()!,
                        x.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number
                            ? priceProp.GetDecimal()
                            : null))
                    .ToList()
                : [];
            return new ReceiptExtraction(merchant, args.GetProperty("total").GetDecimal(), items);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Receipt vision extraction failed");
            return null;
        }
    }
}
