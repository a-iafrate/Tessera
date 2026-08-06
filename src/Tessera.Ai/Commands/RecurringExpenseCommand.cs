using Tessera.Core.Reminders;

namespace Tessera.Ai.Commands;

public abstract record RecurringExpenseCommand
{
    private RecurringExpenseCommand() { }

    public sealed record ListActive : RecurringExpenseCommand;

    public sealed record Create(
        RecurrenceFrequency Frequency, bool AutoRegister, string AmountText, string Description) : RecurringExpenseCommand;
}
