namespace Tessera.Core.Channels;

public record ChannelCapabilities(
    bool SupportsGroups,
    bool SupportsInlineKeyboard,
    bool SupportsProactiveFree,
    bool SupportsDeepLinkPayload,
    // Whether this channel should get the real-time fan-out NotificationService/
    // NotificationAggregationFlushJob do for other members' actions (docs/13-piano-miglioramenti.md,
    // C1/C3). True for every channel that existed before email — defaulted so TelegramChannel
    // and WebChannel don't need to change. Email is the first channel to say no: proactive and
    // free to send, but docs/04-costi.md treats anything more frequent than the daily digest as
    // spam there specifically, unlike Telegram where per-event pings cost nothing and stay
    // reasonable once aggregated (C3).
    bool SupportsRealTimeNotifications = true);
