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
    public void Compute_ReturnsTheMinimumOfTheThreeConstraints(
        ProviderAccessRole providerRole, CalendarShareLevel shareLevel, AccessLevel membershipPermission, AccessLevel expected)
    {
        var result = EffectiveCalendarLevel.Compute(providerRole, shareLevel, membershipPermission);

        Assert.Equal(expected, result);
    }
}
