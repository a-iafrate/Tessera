using Telegram.Bot;
using Telegram.Bot.Types;
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

    public async Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(
            rows.Select(row => row.Select(c => InlineKeyboardButton.WithCallbackData(c.Text, c.Value))));

        await client.SendMessage(to.ExternalChatId, text, replyMarkup: keyboard, cancellationToken: ct);
    }

    public async Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct)
    {
        if (!int.TryParse(messageId, out var msgId))
        {
            return;
        }

        var keyboard = rows.Count == 0
            ? null
            : new InlineKeyboardMarkup(rows.Select(row => row.Select(c => InlineKeyboardButton.WithCallbackData(c.Text, c.Value))));

        try
        {
            await client.EditMessageText(to.ExternalChatId, msgId, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (Exception)
        {
            // Best-effort refresh — the check/remove already succeeded regardless of whether
            // Telegram accepts this edit (edit window expired, message deleted, "not modified").
        }
    }

    public async Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct)
    {
        await client.SendPhoto(to.ExternalChatId, InputFile.FromUri(new Uri(photoUrl)), caption: caption, cancellationToken: ct);
    }

    public async Task SendDocumentAsync(ChannelAddress to, string fileUrl, string fileName, string? caption, CancellationToken ct)
    {
        await client.SendDocument(to.ExternalChatId, InputFile.FromUri(new Uri(fileUrl)), caption: caption, cancellationToken: ct);
    }

    public async Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct)
    {
        var file = await client.GetFile(fileId, ct);
        var stream = new MemoryStream();
        await client.DownloadFile(file.FilePath!, stream, ct);
        stream.Position = 0;
        return stream;
    }
}
