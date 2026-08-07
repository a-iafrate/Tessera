namespace Tessera.Core.Channels;

public enum GroupLifecycleEventType
{
    BotAdded,
    BotRemoved,
    ChatMigrated,
}

// OldChatId is only set for ChatMigrated — the group's chat_id before the migration
// (docs/03-integrazioni.md).
public sealed record GroupLifecycleEvent(GroupLifecycleEventType Type, string? OldChatId = null);
