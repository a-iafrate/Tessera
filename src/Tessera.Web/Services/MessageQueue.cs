using System.Threading.Channels;
using Tessera.Core.Channels;

namespace Tessera.Web.Services;

public sealed class MessageQueue
{
    private readonly Channel<InboundMessage> channel = Channel.CreateUnbounded<InboundMessage>();

    public ValueTask EnqueueAsync(InboundMessage message, CancellationToken ct) =>
        channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<InboundMessage> ReadAllAsync(CancellationToken ct) =>
        channel.Reader.ReadAllAsync(ct);
}
