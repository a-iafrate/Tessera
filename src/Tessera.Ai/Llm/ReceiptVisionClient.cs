using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Tessera.Ai.Llm;

public sealed record ReceiptExtraction(string? Merchant, decimal Total);

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
        You read a photo of a shopping receipt. Extract the merchant name (if legible) and the
        total amount actually paid. Call extract_receipt exactly once with what you can read.
        If the image isn't a legible receipt, don't call the tool — reply with a short
        plain-text message saying so instead.
        """;

    private static readonly ChatTool Tool = ChatTool.CreateFunctionTool(
        ExtractReceiptTool,
        "Report the merchant and total amount read from a receipt photo.",
        BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "merchant": { "type": "string", "description": "The merchant/store name as printed, or omitted if illegible." },
                "total": { "type": "number", "description": "The total amount actually paid, as a plain decimal number." }
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
            return new ReceiptExtraction(merchant, args.GetProperty("total").GetDecimal());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Receipt vision extraction failed");
            return null;
        }
    }
}
