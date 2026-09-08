using Tessera.Core.Calendars;
using Tessera.Core.Spaces;

namespace Tessera.Core.Tests.Calendars;

public class EffectiveCalendarLevelTests
{
    [Theory]
    // The three inputs agree — the effective level is just that level.
    [InlineData(ProviderAccessRole.Reader, CalendarShareLevel.Details, AccessLevel.Read, AccessLevel.Read)]
    [InlineData(ProviderAccessRole.Writer, CalendarShareLevel.Write, AccessLevel.Write, AccessLevel.Write)]
    // Provider grants Reader but the mapping says Write — the provider constraint wins.
    [InlineData(ProviderAccessRole.Reader, CalendarShareLevel.Write, AccessLevel.Write, AccessLevel.Read)]
    // Mapping is Availability-only regardless of what the provider and membership allow.
    [InlineData(ProviderAccessRole.Owner, CalendarShareLevel.Availability, AccessLevel.Write, AccessLevel.Availability)]
    // Membership permission is the tightest constraint.
    [InlineData(ProviderAccessRole.Owner, CalendarShareLevel.Write, AccessLevel.Read, AccessLevel.Read)]
    // Membership has no permission at all on Calendar — nothing is visible regardless of the rest.
    [InlineData(ProviderAccessRole.Owner, CalendarShareLevel.Write, AccessLevel.None, AccessLevel.None)]
    // FreeBusyReader caps the result at Availability even if everything else allows more.
    [InlineData(ProviderAccessRole.FreeBusyReader, CalendarShareLevel.Write, AccessLevel.Write, AccessLevel.Availability)]
    // A space Admin does not bypass a calendar-level restriction (docs/07-compliance.md's
    // privacy point for freebusy.query only makes sense if this holds) — unlike
    // AccessPolicy.CanAsync, where IsOwner *does* bypass everything, calendar access has no
    // such shortcut: it's always the strict minimum of all three.
    [InlineData(ProviderAccessRole.FreeBusyReader, CalendarShareLevel.Write, AccessLevel.Admin, AccessLevel.Availability)]
    public void Compute_ReturnsTheMinimumOfTheThreeConstraints(
        ProviderAccessRole providerRole, CalendarShareLevel shareLevel, AccessLevel membershipPermission, AccessLevel expected)
    {
        var result = EffectiveCalendarLevel.Compute(providerRole, shareLevel, membershipPermission);

        Assert.Equal(expected, result);
    }

    // Isolates each of the three mapping tables in turn, by maxing out the other two
    // constraints (CalendarShareLevel.Write, AccessLevel.Admin — both effectively "no
    // constraint" once nothing lower is in play) so only the value under test can bind.
    // Expectations are stated directly from ProviderAccessRole's own doc comment, not
    // re-derived from Compute's switch statement, so an inverted or dropped case there is
    // still caught here.
    [Theory]
    [InlineData(ProviderAccessRole.FreeBusyReader, AccessLevel.Availability)]
    [InlineData(ProviderAccessRole.Reader, AccessLevel.Read)]
    [InlineData(ProviderAccessRole.Writer, AccessLevel.Write)]
    // Owner is capped at Write, not treated as a level above it — there's no "Admin" concept
    // for calendar access (docs/02-modello-dati.md).
    [InlineData(ProviderAccessRole.Owner, AccessLevel.Write)]
    public void Compute_MapsProviderRole_WhenItIsTheOnlyBindingConstraint(ProviderAccessRole providerRole, AccessLevel expected)
    {
        var result = EffectiveCalendarLevel.Compute(providerRole, CalendarShareLevel.Write, AccessLevel.Admin);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(CalendarShareLevel.Availability, AccessLevel.Availability)]
    [InlineData(CalendarShareLevel.Details, AccessLevel.Read)]
    [InlineData(CalendarShareLevel.Write, AccessLevel.Write)]
    public void Compute_MapsShareLevel_WhenItIsTheOnlyBindingConstraint(CalendarShareLevel shareLevel, AccessLevel expected)
    {
        var result = EffectiveCalendarLevel.Compute(ProviderAccessRole.Owner, shareLevel, AccessLevel.Admin);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(AccessLevel.None, AccessLevel.None)]
    [InlineData(AccessLevel.Availability, AccessLevel.Availability)]
    [InlineData(AccessLevel.Read, AccessLevel.Read)]
    [InlineData(AccessLevel.Write, AccessLevel.Write)]
    // Admin isn't passed through as-is: the ceiling from provider/share level is Write, so
    // membershipPermission binds exactly up to Write and no further.
    [InlineData(AccessLevel.Admin, AccessLevel.Write)]
    public void Compute_PassesThroughMembershipPermission_UpToTheProviderAndShareLevelCeiling(
        AccessLevel membershipPermission, AccessLevel expected)
    {
        var result = EffectiveCalendarLevel.Compute(ProviderAccessRole.Owner, CalendarShareLevel.Write, membershipPermission);

        Assert.Equal(expected, result);
    }

    // Defensive: an out-of-range enum value (never produced by this codebase today, but not
    // guarded against at the type level either) must degrade to no access, not to whatever
    // Math.Min happens to do with a stray 0 — the default branch in each switch is what
    // provides this, and it's easy to lose silently when a case is added or reordered.
    [Fact]
    public void Compute_TreatsAnUndefinedProviderRole_AsNoAccess()
    {
        var result = EffectiveCalendarLevel.Compute((ProviderAccessRole)0, CalendarShareLevel.Write, AccessLevel.Admin);

        Assert.Equal(AccessLevel.None, result);
    }

    [Fact]
    public void Compute_TreatsAnUndefinedShareLevel_AsNoAccess()
    {
        var result = EffectiveCalendarLevel.Compute(ProviderAccessRole.Owner, (CalendarShareLevel)0, AccessLevel.Admin);

        Assert.Equal(AccessLevel.None, result);
    }
}
