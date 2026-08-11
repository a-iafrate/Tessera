using Tessera.Core.Spaces;

namespace Tessera.Core.Users;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string PreferredCulture { get; set; } = "en";
    public string? TimeZoneId { get; set; }
    public Guid? DefaultSpaceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Local hour the daily digest should arrive at — "8 in the morning" is a different UTC
    // instant per TimeZoneId, so the job polls rather than firing at one fixed time
    // (docs/01-architettura.md). LastDigestSentFor is the idempotency guard, same pattern
    // as Budget.LastAlertedFor / RecurringExpense.LastGeneratedFor.
    public int DigestHourLocal { get; set; } = 8;
    public DateOnly? LastDigestSentFor { get; set; }

    // Onboarding progression (docs/10-conversazione.md): counts content-creating actions
    // (shopping.add, expense record, reminder create) to drive the one-hint-at-a-time
    // discovery sequence and the one-time sharing prompt after the third action.
    public int UsefulActionCount { get; set; }
    public bool SharingPromptShown { get; set; }

    public ICollection<ChannelIdentity> ChannelIdentities { get; set; } = [];
    public ICollection<LinkedAccount> LinkedAccounts { get; set; } = [];
    public ICollection<Membership> Memberships { get; set; } = [];
}
