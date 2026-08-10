namespace Tessera.Data;

// RemainingText is the message text with an explicit "in <space name>" suffix stripped, if
// step 1 of the precedence chain matched — the caller parses the command/item text from
// this, not the original (docs/02-modello-dati.md).
//
// PermissionDeniedSpaceId is set when the user explicitly named a space they're a member of,
// but lack the required permission for this resource there (docs/10-conversazione.md: name
// the space and the missing permission, don't silently act somewhere else). SpaceId in that
// case is the best fallback candidate from the normal chain, not the denied space.
public sealed record SpaceResolution(
    Guid? SpaceId, string? RemainingText, IReadOnlyList<Guid> AmbiguousCandidates, Guid? PermissionDeniedSpaceId = null)
{
    public bool IsAmbiguous => AmbiguousCandidates.Count > 1;
}
