using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;

namespace Tessera.Channels;

// What the /chat page renders — one per outbound send. MessageId lets a later
// EditListMessageAsync target this exact bubble; IsEdit tells the page to replace an existing
// bubble instead of appending a new one (the same "refresh a list message in place" pattern
// Telegram uses, but without a platform message id to piggyback on, so this channel mints its
// own). Rows is never null, only empty — an edit with no rows means "remove the buttons."
public sealed record WebChatEvent(
    string MessageId,
    bool IsEdit,
    string? Text,
    IReadOnlyList<IReadOnlyList<Choice>> Rows,
    string? PhotoUrl,
    string? DocumentUrl,
    string? DocumentFileName);

// The web console's own chat page (docs/06-roadmap.md: web chat channel) — no external
// provider, no webhook, no HTTP call to deliver a reply: outbound sends land in a per-chat
// in-memory mailbox that the Blazor page reads from live while it's open. ExternalChatId is
// the user's own Guid (LinkService.EnsureWebIdentityAsync) — one mailbox per logged-in user,
// not per browser tab.
//
// Web push (docs/13-piano-miglioramenti.md, C2) is additive to that mailbox, not a replacement
// for it: when nobody has the page open to read from the mailbox, Post falls back to any
// browser/device push subscriptions the user has registered instead of silently dropping the
// message. IServiceScopeFactory is needed because this class is a singleton (the mailboxes have
// to outlive any single request) but IPushSubscriptionRepository is backed by the per-request
// scoped DbContext — same "open a scope on demand" shape as every scheduled job.
public sealed class WebChannel(IServiceScopeFactory scopeFactory, IPushSender? pushSender = null) : IChannel
{
    private readonly ConcurrentDictionary<string, Channel<WebChatEvent>> mailboxes = new();

    // A browser-picked file has its bytes available immediately in the /chat page, unlike
    // Telegram where InboundMedia.FileId is something the provider resolves later — so the
    // page stages the bytes here under a fresh id before enqueueing the InboundMessage, and
    // DownloadMediaAsync (below) hands them back the one time MessageProcessor asks for them.
    // Consumed entries are removed immediately; an entry only lingers if the message that
    // referenced it never reaches the download step (e.g. an exception first) — acceptable for
    // a v2 feature, not worth a background sweep yet.
    private readonly ConcurrentDictionary<string, byte[]> stagedUploads = new();

    public string Name => "web";

    public ChannelCapabilities Capabilities { get; } = new(
        SupportsGroups: false,
        SupportsInlineKeyboard: true,
        SupportsProactiveFree: true,
        SupportsDeepLinkPayload: false);

    // Opens (or replaces) the mailbox for this chat and streams events from it — called once
    // per page load. Replacing rather than reusing means only the most recently opened tab for
    // a given user receives live updates; several simultaneous tabs on the same account is a
    // known v1 limitation, not a goal (docs/06-roadmap.md).
    public IAsyncEnumerable<WebChatEvent> Subscribe(string chatId, CancellationToken ct)
    {
        var mailbox = Channel.CreateUnbounded<WebChatEvent>();
        mailboxes[chatId] = mailbox;
        return mailbox.Reader.ReadAllAsync(ct);
    }

    public void Unsubscribe(string chatId)
    {
        if (mailboxes.TryRemove(chatId, out var mailbox))
        {
            mailbox.Writer.TryComplete();
        }
    }

    public Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, [], null, null, null), ct);

    public Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, [choices], null, null, null), ct);

    public Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, rows, null, null, null), ct);

    public Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        Post(to, new WebChatEvent(messageId, IsEdit: true, text, rows, null, null, null), ct);

    public Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, caption, [], photoUrl, null, null), ct);

    public Task SendDocumentAsync(ChannelAddress to, string fileUrl, string fileName, string? caption, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, caption, [], null, fileUrl, fileName), ct);

    // Called once per staged upload — see StageUpload below.
    public Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct)
    {
        if (!stagedUploads.TryRemove(fileId, out var bytes))
        {
            throw new InvalidOperationException($"No staged upload for file id '{fileId}' — it may have already been consumed.");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    // Called by the /chat page right after the user picks a file, before enqueueing the
    // InboundMessage that references the returned id as its InboundMedia.FileId.
    public string StageUpload(byte[] bytes)
    {
        var fileId = NewId();
        stagedUploads[fileId] = bytes;
        return fileId;
    }

    private async Task Post(ChannelAddress to, WebChatEvent evt, CancellationToken ct)
    {
        if (mailboxes.TryGetValue(to.ExternalChatId, out var mailbox))
        {
            mailbox.Writer.TryWrite(evt);
            return;
        }

        // No open tab to deliver to live — fall back to push. Skipped for edits: there's no
        // bubble on screen to refresh if nobody had the page open when it was first sent, so a
        // push notification about an edit would reference something the user never saw.
        if (pushSender is null || evt.IsEdit || !Guid.TryParse(to.ExternalChatId, out var userId))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();
        var body = BuildPushBody(evt);

        foreach (var subscription in await subscriptions.GetForUserAsync(userId, ct))
        {
            try
            {
                await pushSender.SendAsync(subscription.Endpoint, subscription.P256dh, subscription.Auth, "Tessera", body, "/chat", ct);
            }
            catch (PushSubscriptionGoneException)
            {
                await subscriptions.RemoveAsync(subscription.Endpoint, ct);
            }
        }
    }

    private static string BuildPushBody(WebChatEvent evt)
    {
        var text = evt.Text ?? (evt.PhotoUrl is not null ? "📎" : evt.DocumentUrl is not null ? $"📎 {evt.DocumentFileName}" : "");
        const int maxLength = 300;
        return text.Length > maxLength ? string.Concat(text.AsSpan(0, maxLength), "…") : text;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
