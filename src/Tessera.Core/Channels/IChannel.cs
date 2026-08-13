namespace Tessera.Core.Channels;

public interface IChannel
{
    string Name { get; }

    Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct);

    Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct);

    // Fetches the raw bytes for an inbound attachment (docs/03-integrazioni.md) — every
    // provider requires its own get-file-info-then-download dance (Telegram: getFile then a
    // separate download URL), so this has to live behind the channel abstraction rather than
    // assuming the caller already has a byte stream from the webhook payload alone.
    Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct);

    ChannelCapabilities Capabilities { get; }
}
