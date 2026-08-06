using Tessera.Core.Reminders;

namespace Tessera.Core.Expenses;

public class RecurringExpense
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Description { get; set; } = null!;
    public Guid? CategoryId { get; set; }
    public RecurrenceRule Recurrence { get; set; } = null!;
    public DateOnly? EndsOn { get; set; }

    // Creates the Expense automatically, or degrades to a reminder for variable-amount
    // bills where the due date is known but not the figure (docs/02-modello-dati.md).
    public bool AutoRegister { get; set; } = true;

    // Idempotency for the generation job: checked against, not "does a similar expense
    // already exist" — a separate, later checklist item (the scheduled worker).
    public DateOnly? LastGeneratedFor { get; set; }
}
