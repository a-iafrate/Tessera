namespace Tessera.Core.Calendars;

// The level a mapped calendar is exposed at within one space — separate from ProviderAccessRole
// (what the provider grants) and from MembershipPermission (what a member is allowed to see);
// the effective level is the minimum of the three (docs/02-modello-dati.md).
public enum CalendarShareLevel
{
    Availability = 1,
    Details = 2,
    Write = 3,
}
