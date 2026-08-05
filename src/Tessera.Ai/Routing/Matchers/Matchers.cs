namespace Tessera.Ai.Routing.Matchers;

public static class Matchers
{
    public static IReadOnlyList<IIntentMatcher> All { get; } =
    [
        new ItShoppingAddMatcher(),
        new ItShoppingShowMatcher(),
        new ItExpensesQueryMatcher(),
        new EnShoppingAddMatcher(),
        new EnExpensesQueryMatcher(),
    ];
}
