using Telegram.Bot.Types;
using Tessera.Core.Channels;

namespace Tessera.Web.Endpoints;

internal static class UpdateExtensions
{
    // my_chat_member (group lifecycle, docs/03-integrazioni.md) and edited_message land
    // here in a later step.
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
                CallbackData: callback.Data);
        }

        var message = update.Message;
        if (message is null)
        {
            return null;
        }

        return new InboundMessage(
            ChannelName: "telegram",
            ExternalChatId: message.Chat.Id.ToString(),
            ExternalUserId: message.From?.Id.ToString(),
            Text: message.Text,
            Media: [],
            ProviderMessageId: update.Id.ToString(),
            SentAt: message.Date);
    }
}
