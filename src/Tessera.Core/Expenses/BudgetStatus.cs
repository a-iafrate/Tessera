namespace Tessera.Core.Expenses;

public sealed record BudgetStatus(Guid? CategoryId, decimal Spent, decimal Limit);
