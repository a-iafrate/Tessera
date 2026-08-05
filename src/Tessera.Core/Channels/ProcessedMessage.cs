namespace Tessera.Core.Channels;

public class ProcessedMessage
{
    public string ChannelName { get; set; } = null!;
    public string ProviderMessageId { get; set; } = null!;
    public DateTimeOffset ProcessedAt { get; set; }
}
