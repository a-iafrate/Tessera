namespace Tessera.Core.Spaces;

public class Space
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public Guid OwnerId { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTimeOffset CreatedAt { get; set; }

    public string? GroupChatId { get; set; }
    public string? PreviousGroupChatId { get; set; }
    public string? GroupChannelName { get; set; }

    public ICollection<Membership> Memberships { get; set; } = [];
}
