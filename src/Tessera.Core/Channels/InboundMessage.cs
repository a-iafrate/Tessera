namespace Tessera.Core.Channels;

public sealed record InboundMessage(
    string ChannelName,
    string ExternalChatId,
    string? ExternalUserId,
    string? Text,
    IReadOnlyList<InboundMedia> Media,
    string ProviderMessageId,
    DateTimeOffset SentAt);
