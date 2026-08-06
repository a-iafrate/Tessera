namespace Tessera.Core.Expenses;

// Telegram's callback_data caps at 64 bytes — too little for free-text category/merchant,
// so the pending choice (and the context needed to finish recording once resolved) lives
// here instead, referenced by a short id. Short-lived, like LinkToken (docs/02-modello-dati.md).
public class PendingExpenseConfirmation
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public decimal CandidateAsGrouped { get; set; }
    public decimal CandidateAsDecimal { get; set; }
    public string? CategoryText { get; set; }
    public string? MerchantText { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
