namespace Tessera.Core.Channels;

public interface IChannel
{
    string Name { get; }

    Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct);

    Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct);

    // Multiple buttons per row (e.g. ✓ and 🗑 side by side for the same list item) —
    // SendChoicesAsync puts one button per row, which can't express two actions for one item.
    Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct);

    // Refreshes a previously-sent list message in place after a button tap acted on one of its
    // rows — without this, a checked/removed item's button stays visible and stale until the
    // next /list. Best-effort by design: an edit can fail (message too old, deleted, or
    // unchanged) and implementations swallow that, since the underlying action already
    // succeeded regardless of whether the message could be refreshed. Empty rows means "remove
    // the keyboard" (the list is now empty).
    Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct);

    // Shows an image attachment inline rather than as a link — photoUrl is a short-lived SAS
    // URL from IBlobStorage, so the channel fetches the image itself rather than us downloading
    // and re-uploading the bytes.
    Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct);

    // Fetches the raw bytes for an inbound attachment (docs/03-integrazioni.md) — every
    // provider requires its own get-file-info-then-download dance (Telegram: getFile then a
    // separate download URL), so this has to live behind the channel abstraction rather than
    // assuming the caller already has a byte stream from the webhook payload alone.
    Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct);

    ChannelCapabilities Capabilities { get; }
}
