namespace Tessera.Core.Spaces;

public class Membership
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public bool IsOwner { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public ICollection<MembershipPermission> Permissions { get; set; } = [];
}
