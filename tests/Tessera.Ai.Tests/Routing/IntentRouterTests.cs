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
        { "it", "cosa c'è in lista?", "shopping.show" },
        { "it", "spunta il latte", "shopping.check" },
        { "it", "rimuovi il pane", "shopping.remove" },
        { "it", "togli il pane dalla lista", "shopping.remove" },
        { "it", "svuota la lista", "shopping.clear" },
        { "it", "svuota la lista!", "shopping.clear" },
        { "it", "quanto ho speso a gennaio", "expenses.query" },
        { "it", "ho speso 15 euro", "expenses.add" },
        { "it", "spesa di 20,50 euro per la benzina", "expenses.add" },
        { "it", "registra una spesa di 30 euro", "expenses.add" },
        { "it", "spesa di 20 euro da Esselunga", "expenses.add" },
        { "it", "quanto ho speso in benzina", "expenses.query.category" },
        { "it", "quanto ho speso per la spesa alimentare?", "expenses.query.category" },
        { "it", "ricordami di comprare il latte domani", "reminders.natural" },
        { "it", "ricordati che serve il pane", "reminders.natural" },
        { "it", "aggiungi la macchina domani alle 17 in calendario", "calendar.natural" },
        { "it", "metti la riunione con Marco in calendario", "calendar.natural" },
        { "it", "crea un evento per la cena di domani", "calendar.natural" },
        { "it", "annulla", "undo" },
        { "it", "annulla operazione", "undo" },
        { "it", "no aspetta", "undo" },

        // italiano — devono cadere a L3
        { "it", "finito il detersivo", null },
        { "it", "sposta la riunione con Marco e avvisalo", null },
        { "it", "aggiungi nota test", null },
        { "it", "aggiungi una nota che dice test", null },
        { "it", "metti note su questo", null },

        // inglese — L2
        { "en", "add milk", "shopping.add" },
        { "en", "put bread on the list", "shopping.add" },
        { "en", "show me the list", "shopping.show" },
        { "en", "what's on the list?", "shopping.show" },
        { "en", "check off milk", "shopping.check" },
        { "en", "remove bread", "shopping.remove" },
        { "en", "clear the list", "shopping.clear" },
        { "en", "clear the list!", "shopping.clear" },
        { "en", "how much did I spend in January", "expenses.query" },
        { "en", "spent 20 euros", "expenses.add" },
        { "en", "record an expense of 15 for groceries", "expenses.add" },
        { "en", "log 30", "expenses.add" },
        { "en", "spent 20 at Tesco", "expenses.add" },
        { "en", "how much did I spend on groceries", "expenses.query.category" },
        { "en", "how much did I spend on groceries?", "expenses.query.category" },
        { "en", "remind me to buy milk tomorrow", "reminders.natural" },
        { "en", "remind me that the rent is due", "reminders.natural" },
        { "en", "add dentist appointment to my calendar tomorrow at 5pm", "calendar.natural" },
        { "en", "schedule a meeting with Marco tomorrow", "calendar.natural" },
        { "en", "create an event for dinner tomorrow", "calendar.natural" },
        { "en", "undo", "undo" },
        { "en", "cancel that", "undo" },
        { "en", "no wait", "undo" },

        // inglese — L3
        { "en", "we're out of detergent", null },
        { "en", "add note test", null },
        { "en", "add a note that says test", null },

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
    [InlineData("aggiungi il latte?", "il latte")]
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
    public void TryRoute_estrae_importo_e_categoria_in_italiano()
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute("spesa di 20,50 euro per la benzina", "it");

        Assert.Equal("expenses.add", match?.Intent);
        Assert.Equal("20,50", match?.Slots["amount"]);
        Assert.Equal("la benzina", match?.Slots["category"]);
    }

    [Fact]
    public void TryRoute_estrae_importo_senza_categoria_in_italiano()
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute("ho speso 15 euro", "it");

        Assert.Equal("expenses.add", match?.Intent);
        Assert.Equal("15", match?.Slots["amount"]);
        Assert.False(match?.Slots.ContainsKey("category"));
    }

    [Fact]
    public void TryRoute_estrae_importo_con_punto_decimale_in_inglese()
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute("record an expense of 15.50 for groceries", "en");

        Assert.Equal("expenses.add", match?.Intent);
        Assert.Equal("15.50", match?.Slots["amount"]);
        Assert.Equal("groceries", match?.Slots["category"]);
    }

    [Fact]
    public void TryRoute_estrae_merchant_senza_categoria_in_italiano()
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute("spesa di 20 euro da Esselunga", "it");

        Assert.Equal("expenses.add", match?.Intent);
        Assert.Equal("20", match?.Slots["amount"]);
        Assert.Equal("Esselunga", match?.Slots["merchant"]);
        Assert.False(match?.Slots.ContainsKey("category"));
    }

    [Fact]
    public void TryRoute_estrae_merchant_senza_categoria_in_inglese()
    {
        var router = new IntentRouter(Matchers.All);

        var match = router.TryRoute("spent 20 at Tesco", "en");

        Assert.Equal("expenses.add", match?.Intent);
        Assert.Equal("Tesco", match?.Slots["merchant"]);
        Assert.False(match?.Slots.ContainsKey("category"));
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
