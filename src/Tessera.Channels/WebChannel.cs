using System.Collections.Concurrent;
using System.Threading.Channels;
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
public sealed class WebChannel : IChannel
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
    // known v1 limitation, not a goal.
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
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, [], null, null, null));

    public Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, [choices], null, null, null));

    public Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, text, rows, null, null, null));

    public Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct) =>
        Post(to, new WebChatEvent(messageId, IsEdit: true, text, rows, null, null, null));

    public Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, caption, [], photoUrl, null, null));

    public Task SendDocumentAsync(ChannelAddress to, string fileUrl, string fileName, string? caption, CancellationToken ct) =>
        Post(to, new WebChatEvent(NewId(), IsEdit: false, caption, [], null, fileUrl, fileName));

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

    // Best-effort, like Telegram's own swallowed edit failures: if nobody has the page open
    // there's no one to deliver to — not an error, the same as a phone that's turned off.
    private Task Post(ChannelAddress to, WebChatEvent evt)
    {
        if (mailboxes.TryGetValue(to.ExternalChatId, out var mailbox))
        {
            mailbox.Writer.TryWrite(evt);
        }

        return Task.CompletedTask;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
