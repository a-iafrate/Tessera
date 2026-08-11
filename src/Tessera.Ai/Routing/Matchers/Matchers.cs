namespace Tessera.Ai.Routing.Matchers;

public static class Matchers
{
    public static IReadOnlyList<IIntentMatcher> All { get; } =
    [
        new ItUndoMatcher(),
        new EnUndoMatcher(),
        // Calendar matchers run before shopping.add — "aggiungi"/"add" collide with both.
        new ItCalendarEventNaturalLanguageMatcher(),
        new ItShoppingAddMatcher(),
        new ItShoppingShowMatcher(),
        new ItShoppingCheckMatcher(),
        new ItShoppingRemoveMatcher(),
        new ItShoppingClearMatcher(),
        new ItExpenseAddMatcher(),
        new ItExpensesQueryByCategoryMatcher(),
        new ItExpensesQueryMatcher(),
        new ItReminderNaturalLanguageMatcher(),
        new EnCalendarEventNaturalLanguageMatcher(),
        new EnShoppingAddMatcher(),
        new EnShoppingShowMatcher(),
        new EnShoppingCheckMatcher(),
        new EnShoppingRemoveMatcher(),
        new EnShoppingClearMatcher(),
        new EnExpenseAddMatcher(),
        new EnExpensesQueryByCategoryMatcher(),
        new EnExpensesQueryMatcher(),
        new EnReminderNaturalLanguageMatcher(),
    ];
}
