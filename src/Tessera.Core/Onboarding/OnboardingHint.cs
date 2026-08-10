namespace Tessera.Core.Onboarding;

// Discovery hints retire on their own (docs/10-conversazione.md): shown up to a few times,
// then dropped once the user has either seen enough of them or already used the feature
// they point at. One row per (user, hint), not a single counter, since Phase 1 has more than
// one feature worth suggesting.
public class OnboardingHint
{
    public Guid UserId { get; set; }
    public string HintKey { get; set; } = null!;
    public int ShownCount { get; set; }
    public bool Dismissed { get; set; }
}
