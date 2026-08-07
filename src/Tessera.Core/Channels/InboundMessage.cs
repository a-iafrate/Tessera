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
    string? CallbackData = null,
    // Set instead of Text/CallbackData for group lifecycle signals — my_chat_member and
    // migrate_to/from_chat_id (docs/03-integrazioni.md) — which aren't a message from a user.
    GroupLifecycleEvent? LifecycleEvent = null,
    // Distinguishes a group chat from a private one — /link means something different in
    // each (docs/03-integrazioni.md: re-associate the group vs. "you're already linked").
    bool IsGroupChat = false);
