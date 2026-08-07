using System.Text.Json;

namespace Tessera.Ai.Llm;

public sealed record LlmToolCall(string Name, JsonElement Arguments);

// Exactly one of the two is set: a recognized action, or a short plain-text reply (a
// clarifying question, or a "that's not something I can do yet") already in the user's
// language, per the system prompt's instruction.
public sealed record LlmResult(string? ReplyText, LlmToolCall? ToolCall);
