namespace Tessera.Core.Spaces;

public class MembershipPermission
{
    public Guid MembershipId { get; set; }
    public ResourceKind Resource { get; set; }
    public AccessLevel Level { get; set; }
}
