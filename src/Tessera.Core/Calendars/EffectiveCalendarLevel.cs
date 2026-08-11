using Tessera.Core.Spaces;

namespace Tessera.Core.Calendars;

// Hard rule (CLAUDE.md #15, docs/02-modello-dati.md): the effective calendar level is the
// minimum of three independent constraints, computed in exactly this one place. Getting it
// wrong either proposes an action the provider will reject, or exposes event details that
// should have stayed availability-only.
public static class EffectiveCalendarLevel
{
    public static AccessLevel Compute(ProviderAccessRole providerRole, CalendarShareLevel shareLevel, AccessLevel membershipPermission)
    {
        var fromProvider = providerRole switch
        {
            ProviderAccessRole.FreeBusyReader => AccessLevel.Availability,
            ProviderAccessRole.Reader => AccessLevel.Read,
            ProviderAccessRole.Writer or ProviderAccessRole.Owner => AccessLevel.Write,
            _ => AccessLevel.None,
        };

        var fromShareLevel = shareLevel switch
        {
            CalendarShareLevel.Availability => AccessLevel.Availability,
            CalendarShareLevel.Details => AccessLevel.Read,
            CalendarShareLevel.Write => AccessLevel.Write,
            _ => AccessLevel.None,
        };

        return Min(fromProvider, fromShareLevel, membershipPermission);
    }

    private static AccessLevel Min(AccessLevel a, AccessLevel b, AccessLevel c) =>
        (AccessLevel)Math.Min((int)a, Math.Min((int)b, (int)c));
}
