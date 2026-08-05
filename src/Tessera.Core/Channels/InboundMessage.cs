namespace Tessera.Core.Channels;

public sealed record InboundMessage(
    string ChannelName,
    string ExternalChatId,
    string? ExternalUserId,
    string? Text,
    IReadOnlyList<InboundMedia> Media,
    string ProviderMessageId,
    DateTimeOffset SentAt,
    // Set instead of Text when this is an inline-keyboard tap (L1, docs/05-ottimizzazioni.md) —
    // already-structured, zero interpretation, never routed through the intent matcher.
    string? CallbackData = null);
