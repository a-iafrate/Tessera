using Tessera.Core.Reminders;

namespace Tessera.Ai.Commands;

public abstract record RemindCommand
{
    private RemindCommand() { }

    public sealed record ListPending : RemindCommand;

    public sealed record CreateOnce(DateOnly Date, TimeOnly? Time, string Text) : RemindCommand;

    public sealed record CreateRecurring(RecurrenceFrequency Frequency, string Text) : RemindCommand;
}
