using Tessera.Core.Abstractions;
using Tessera.Core.Channels;

namespace Tessera.Channels;

// The third IChannel consumer, after Telegram and the web console (docs/13-piano-miglioramenti.md,
// C1) — the one SupportsInlineKeyboard/SupportsProactiveFree/SupportsRealTimeNotifications were
// designed to distinguish: free to send, no buttons, and (unlike the other two) never a target
// for real-time per-event fan-out — only the scheduled daily digest, which is why
// DailyDigestJob calls SendDigestAsync directly instead of going through IChannel.SendTextAsync
// for it. This class stays free of localization/formatting decisions on purpose — like
// TelegramChannel and WebChannel, it only knows how to deliver text it's handed; DailyDigestJob
// (which already has an IStringLocalizer) is responsible for every piece of text passed in here.
public sealed class EmailChannel(IEmailSender emailSender) : IChannel
{
    public string Name => "email";

    public ChannelCapabilities Capabilities { get; } = new(
        SupportsGroups: false,
        SupportsInlineKeyboard: false,
        SupportsProactiveFree: true,
        SupportsDeepLinkPayload: false,
        SupportsRealTimeNotifications: false);

    // Required by IChannel, but SupportsRealTimeNotifications: false means nothing in this
    // codebase calls it today — DailyDigestJob uses SendDigestAsync below instead. Kept as a
    // plain, unstyled fallback rather than throwing, in case a future proactive path ever does
    // want to send plain text to email without going through the digest-specific path.
    public Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct) =>
        emailSender.SendAsync(to.ExternalChatId, "Tessera", EmailHtmlTemplate.WrapPlainText(text), ct);

    public Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct) =>
        throw new NotSupportedException("Email has no inline keyboard (Capabilities.SupportsInlineKeyboard is false).");

    public Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        throw new NotSupportedException("Email has no inline keyboard (Capabilities.SupportsInlineKeyboard is false).");

    public Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        throw new NotSupportedException("A sent email can't be edited in place.");

    public Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct) =>
        emailSender.SendAsync(to.ExternalChatId, "Tessera", EmailHtmlTemplate.WrapImageLink(photoUrl, caption), ct);

    public Task SendDocumentAsync(ChannelAddress to, string fileUrl, string fileName, string? caption, CancellationToken ct) =>
        emailSender.SendAsync(to.ExternalChatId, "Tessera", EmailHtmlTemplate.WrapDocumentLink(fileUrl, fileName, caption), ct);

    public Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct) =>
        throw new NotSupportedException("Email is never an inbound channel — there's no user-sent media to download.");

    // The one real send path today: DailyDigestJob calls this directly (not through IChannel),
    // because a digest email needs a subject line, styled sections and an unsubscribe link —
    // none of which fit IChannel's generic "send this text" contract.
    public Task SendDigestAsync(
        string toEmail, string subject, IReadOnlyList<(string Header, string Body)> sections,
        string unsubscribeUrl, string unsubscribeText, string lang, CancellationToken ct)
    {
        var html = EmailHtmlTemplate.BuildDigest(subject, sections, unsubscribeUrl, unsubscribeText, lang);
        return emailSender.SendAsync(toEmail, subject, html, ct);
    }
}
