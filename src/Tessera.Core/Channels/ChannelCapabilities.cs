namespace Tessera.Core.Channels;

public record ChannelCapabilities(
    bool SupportsGroups,
    bool SupportsInlineKeyboard,
    bool SupportsProactiveFree,
    bool SupportsDeepLinkPayload);
