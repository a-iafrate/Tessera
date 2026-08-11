namespace Tessera.Web.Services;

// Shared between Register.razor (asks at signup, so reminders/digests/calendar events are
// never silently wrong until someone happens to notice) and Profile.razor (lets it be
// corrected later) — one list, so the two pickers can't drift apart.
public static class CommonTimeZones
{
    public static readonly string[] All =
    [
        "Europe/Rome", "Europe/London", "Europe/Paris", "Europe/Berlin", "Europe/Madrid",
        "Europe/Amsterdam", "Europe/Zurich", "Europe/Lisbon", "Europe/Athens", "Europe/Moscow",
        "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles",
        "America/Sao_Paulo", "America/Mexico_City",
        "Asia/Dubai", "Asia/Kolkata", "Asia/Shanghai", "Asia/Tokyo", "Asia/Singapore",
        "Australia/Sydney", "Pacific/Auckland",
        "Africa/Cairo", "Africa/Johannesburg",
        "UTC",
    ];
}
