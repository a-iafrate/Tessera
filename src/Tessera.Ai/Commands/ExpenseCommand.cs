namespace Tessera.Ai.Commands;

// The trivial, deterministic form of /expense — an amount and an optional free-text
// category, no merchant slot (that's the natural-language path's job).
public sealed record ExpenseCommand(string AmountText, string? CategoryText);
