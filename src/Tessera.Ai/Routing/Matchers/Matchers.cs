namespace Tessera.Ai.Routing.Matchers;

public static class Matchers
{
    public static IReadOnlyList<IIntentMatcher> All { get; } =
    [
        new ItShoppingAddMatcher(),
        new ItShoppingShowMatcher(),
        new ItShoppingCheckMatcher(),
        new ItShoppingRemoveMatcher(),
        new ItShoppingClearMatcher(),
        new ItExpenseAddMatcher(),
        new ItExpensesQueryByCategoryMatcher(),
        new ItExpensesQueryMatcher(),
        new EnShoppingAddMatcher(),
        new EnShoppingShowMatcher(),
        new EnShoppingCheckMatcher(),
        new EnShoppingRemoveMatcher(),
        new EnShoppingClearMatcher(),
        new EnExpenseAddMatcher(),
        new EnExpensesQueryByCategoryMatcher(),
        new EnExpensesQueryMatcher(),
    ];
}
