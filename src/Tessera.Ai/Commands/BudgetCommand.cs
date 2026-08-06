namespace Tessera.Ai.Commands;

public abstract record BudgetCommand
{
    private BudgetCommand() { }

    public sealed record ListActive : BudgetCommand;

    public sealed record SetOverall(string AmountText) : BudgetCommand;

    public sealed record SetCategory(string CategoryText, string AmountText) : BudgetCommand;
}
