namespace Tessera.Core.Spaces;

// DisplayNameSnapshot is the point: the name as it was then, not a live reference — so a
// January expense stays attributed to "Marco" even if he later renames or deletes his
// account, and rendering it never has to read a User row that might no longer exist
// (docs/02-modello-dati.md).
public class MembershipArchive
{
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayNameSnapshot { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset LeftAt { get; set; }
    public MembershipEndReason Reason { get; set; }
}
