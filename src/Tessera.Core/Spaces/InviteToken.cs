namespace Tessera.Core.Spaces;

// Mirrors LinkToken's pattern (docs/02-modello-dati.md): the token itself is the
// authentication, single-use, shared out-of-band since no email sender exists yet
// (docs/07-compliance.md). A week-long TTL, not LinkToken's 10 minutes — an invite is
// meant to sit in a chat until the invitee gets to it, not be consumed immediately.
public class InviteToken
{
    public Guid Id { get; set; }
    public string Token { get; set; } = null!;
    public Guid SpaceId { get; set; }
    public Guid InvitedByUserId { get; set; }
    public AccessLevel ShoppingListLevel { get; set; }
    public AccessLevel ExpensesLevel { get; set; }
    public AccessLevel RemindersLevel { get; set; }
    public AccessLevel CalendarLevel { get; set; }
    public AccessLevel NotesLevel { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
