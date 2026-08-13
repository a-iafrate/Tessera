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
    // The id of the message the tapped button was attached to — lets a handler refresh that
    // same message in place (e.g. removing a checked item's row) instead of leaving stale
    // buttons visible until the next /list. Null for anything that isn't a callback tap.
    string? CallbackMessageId = null,
    // Set instead of Text/CallbackData for group lifecycle signals — my_chat_member and
    // migrate_to/from_chat_id (docs/03-integrazioni.md) — which aren't a message from a user.
    GroupLifecycleEvent? LifecycleEvent = null,
    // Distinguishes a group chat from a private one — /link means something different in
    // each (docs/03-integrazioni.md: re-associate the group vs. "you're already linked").
    bool IsGroupChat = false);
