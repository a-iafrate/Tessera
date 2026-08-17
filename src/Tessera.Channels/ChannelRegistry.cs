using Tessera.Core.Channels;

namespace Tessera.Channels;

public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Dictionary<string, IChannel> byName;

    public ChannelRegistry(IEnumerable<IChannel> channels)
    {
        byName = channels.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IChannel? TryGet(string channelName) => byName.GetValueOrDefault(channelName);
}
