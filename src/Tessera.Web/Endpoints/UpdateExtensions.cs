using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Tessera.Core.Channels;

namespace Tessera.Web.Endpoints;

internal static class UpdateExtensions
{
    // edited_message lands here in a later step.
    public static InboundMessage? ToInbound(this Update update)
    {
        if (update.CallbackQuery is { } callback)
        {
            var chatId = callback.Message?.Chat.Id.ToString() ?? callback.From.Id.ToString();
            return new InboundMessage(
                ChannelName: "telegram",
                ExternalChatId: chatId,
                ExternalUserId: callback.From.Id.ToString(),
                Text: null,
                Media: [],
                ProviderMessageId: $"callback:{callback.Id}",
                SentAt: DateTimeOffset.UtcNow,
                CallbackData: callback.Data,
                CallbackMessageId: callback.Message?.MessageId.ToString());
        }

        // Group lifecycle (docs/03-integrazioni.md): the bot's own membership status
        // changing in a chat — not a message from a user.
        if (update.MyChatMember is { } myChatMember)
        {
            GroupLifecycleEventType? type = myChatMember.NewChatMember.Status switch
            {
                ChatMemberStatus.Left or ChatMemberStatus.Kicked => GroupLifecycleEventType.BotRemoved,
                ChatMemberStatus.Member or ChatMemberStatus.Administrator => GroupLifecycleEventType.BotAdded,
                _ => null,
            };
            if (type is null)
            {
                return null;
            }

            return new InboundMessage(
                ChannelName: "telegram",
                ExternalChatId: myChatMember.Chat.Id.ToString(),
                ExternalUserId: myChatMember.From.Id.ToString(),
                Text: null,
                Media: [],
                ProviderMessageId: $"mychatmember:{update.Id}",
                SentAt: myChatMember.Date,
                LifecycleEvent: new GroupLifecycleEvent(type.Value));
        }

        var message = update.Message;
        if (message is null)
        {
            return null;
        }

        // Group → supergroup migration (docs/03-integrazioni.md) arrives as two different
        // service messages depending on timing — both normalize to the same event shape.
        if (message.MigrateToChatId is { } newChatId)
        {
            return new InboundMessage(
                ChannelName: "telegram",
                ExternalChatId: newChatId.ToString(),
                ExternalUserId: message.From?.Id.ToString(),
                Text: null,
                Media: [],
                ProviderMessageId: update.Id.ToString(),
                SentAt: message.Date,
                LifecycleEvent: new GroupLifecycleEvent(GroupLifecycleEventType.ChatMigrated, message.Chat.Id.ToString()));
        }

        if (message.MigrateFromChatId is { } oldChatId)
        {
            return new InboundMessage(
                ChannelName: "telegram",
                ExternalChatId: message.Chat.Id.ToString(),
                ExternalUserId: message.From?.Id.ToString(),
                Text: null,
                Media: [],
                ProviderMessageId: update.Id.ToString(),
                SentAt: message.Date,
                LifecycleEvent: new GroupLifecycleEvent(GroupLifecycleEventType.ChatMigrated, oldChatId.ToString()));
        }

        // A photo message carries its accompanying text as Caption, not Text — Text stays
        // null on a photo/document message regardless of what the user typed alongside it.
        IReadOnlyList<InboundMedia> media = [];
        if (message.Photo is { Length: > 0 } photos)
        {
            // Telegram returns every resolution Telegram itself generated, ascending — the
            // last is the largest, and the only one worth actually storing.
            var largest = photos[^1];
            media = [new InboundMedia("photo", largest.FileId, FileName: null, MimeType: null)];
        }
        else if (message.Document is { } document)
        {
            media = [new InboundMedia("document", document.FileId, document.FileName, document.MimeType)];
        }

        return new InboundMessage(
            ChannelName: "telegram",
            ExternalChatId: message.Chat.Id.ToString(),
            ExternalUserId: message.From?.Id.ToString(),
            Text: message.Text ?? message.Caption,
            Media: media,
            ProviderMessageId: update.Id.ToString(),
            SentAt: message.Date,
            IsGroupChat: message.Chat.Type is ChatType.Group or ChatType.Supergroup);
    }
}
