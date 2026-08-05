using Tessera.Ai.Routing;
using Tessera.Ai.Routing.Matchers;

namespace Tessera.Ai.Tests.Routing;

public class IntentRouterTests
{
    // Every phrase that reaches L3 in production when it could have been handled at L2
    // gets added here (docs/08-setup-sviluppo.md) — this corpus is the most valuable set
    // of tests in the codebase and regresses easily.
    public static TheoryData<string, string, string?> Corpus => new()
    {
        // italiano — gestiti a L2, senza LLM
        { "it", "aggiungi il latte", "shopping.add" },
        { "it", "metti 2 litri di latte nella lista", "shopping.add" },
        { "it", "segna pane", "shopping.add" },
        { "it", "cosa c'è in lista", "shopping.show" },
        { "it", "quanto ho speso a gennaio", "expenses.query" },

        // italiano — devono cadere a L3
        { "it", "ricordati che serve il pane", null },
        { "it", "finito il detersivo", null },
        { "it", "sposta la riunione con Marco e avvisalo", null },

        // inglese — L2
        { "en", "add milk", "shopping.add" },
        { "en", "put bread on the list", "shopping.add" },
        { "en", "how much did I spend in January", "expenses.query" },

        // inglese — L3
        { "en", "we're out of detergent", null },

        // lingua senza matcher — sempre L3, per progetto
        { "de", "milch hinzufügen", null },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TryRoute_classifica_correttamente(string culture, string input, string? expectedIntent)
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute(input, culture);

        Assert.Equal(expectedIntent, match?.Intent);
    }

    [Theory]
    [InlineData("aggiungi il latte", "il latte")]
    [InlineData("metti 2 litri di latte nella lista", "2 litri di latte")]
    [InlineData("segna pane", "pane")]
    public void TryRoute_estrae_lo_slot_item_in_italiano(string input, string expectedSlot)
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute(input, "it");

        Assert.Equal(expectedSlot, match?.Slots["item"]);
    }

    [Theory]
    [InlineData("add milk", "milk")]
    [InlineData("put bread on the list", "bread")]
    public void TryRoute_estrae_lo_slot_item_in_inglese(string input, string expectedSlot)
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute(input, "en");

        Assert.Equal(expectedSlot, match?.Slots["item"]);
    }

    [Fact]
    public void TryRoute_ignora_i_match_sotto_soglia_di_confidenza()
    {
        var lowConfidenceMatcher = new FakeMatcher("it", "fake.intent", confidence: 0.5);
        var router = new IntentRouter([lowConfidenceMatcher]);

        var match = router.TryRoute("qualunque cosa", "it");

        Assert.Null(match);
    }

    private sealed class FakeMatcher(string culture, string intent, double confidence) : IIntentMatcher
    {
        public string Intent => intent;
        public string Culture => culture;

        public IntentMatch? TryMatch(string text) =>
            new(Intent, confidence, new Dictionary<string, string>());
    }
}
