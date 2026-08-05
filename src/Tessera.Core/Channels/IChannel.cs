namespace Tessera.Core.Channels;

public interface IChannel
{
    string Name { get; }

    Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct);

    Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct);

    ChannelCapabilities Capabilities { get; }
}
