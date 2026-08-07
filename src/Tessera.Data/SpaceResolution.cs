namespace Tessera.Data;

// RemainingText is the message text with an explicit "in <space name>" suffix stripped, if
// step 1 of the precedence chain matched — the caller parses the command/item text from
// this, not the original (docs/02-modello-dati.md).
public sealed record SpaceResolution(Guid? SpaceId, string? RemainingText, IReadOnlyList<Guid> AmbiguousCandidates)
{
    public bool IsAmbiguous => AmbiguousCandidates.Count > 1;
}
