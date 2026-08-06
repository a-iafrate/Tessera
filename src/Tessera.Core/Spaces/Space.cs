namespace Tessera.Core.Spaces;

public class Space
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public Guid OwnerId { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTimeOffset CreatedAt { get; set; }

    // Every Space is on exactly one commercial tier — never null, defaults to
    // SystemPlanIds.Free at creation (docs/04-costi.md). Limits enforcement isn't wired
    // up yet; this is the schema it will read from once it is.
    public Guid PlanId { get; set; }

    public string? GroupChatId { get; set; }
    public string? PreviousGroupChatId { get; set; }
    public string? GroupChannelName { get; set; }

    public ICollection<Membership> Memberships { get; set; } = [];
}
