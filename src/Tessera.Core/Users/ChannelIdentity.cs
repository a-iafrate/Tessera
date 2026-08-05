namespace Tessera.Core.Users;

public class ChannelIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ChannelName { get; set; } = null!;
    public string ExternalUserId { get; set; } = null!;
    public string? ExternalChatId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}
