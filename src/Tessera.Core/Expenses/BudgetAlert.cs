namespace Tessera.Core.Expenses;

public sealed record BudgetAlert(Guid? CategoryId, decimal Spent, decimal Limit);
