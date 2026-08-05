using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Tessera.Core.Channels;

namespace Tessera.Channels;

public sealed class TelegramChannel(ITelegramBotClient client) : IChannel
{
    public string Name => "telegram";

    public ChannelCapabilities Capabilities { get; } = new(
        SupportsGroups: true,
        SupportsInlineKeyboard: true,
        SupportsProactiveFree: true,
        SupportsDeepLinkPayload: true);

    public async Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct)
    {
        await client.SendMessage(to.ExternalChatId, text, cancellationToken: ct);
    }

    public async Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(
            choices.Select(c => InlineKeyboardButton.WithCallbackData(c.Text, c.Value)));

        await client.SendMessage(to.ExternalChatId, text, replyMarkup: keyboard, cancellationToken: ct);
    }
}
