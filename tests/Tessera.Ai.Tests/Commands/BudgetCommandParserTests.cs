using Tessera.Ai.Commands;

namespace Tessera.Ai.Tests.Commands;

public class BudgetCommandParserTests
{
    [Fact]
    public void Parse_ritorna_ListActive_per_testo_vuoto()
    {
        var result = BudgetCommandParser.Parse("");

        Assert.IsType<BudgetCommand.ListActive>(result);
    }

    [Fact]
    public void Parse_riconosce_importo_senza_categoria_come_budget_complessivo()
    {
        var result = BudgetCommandParser.Parse("500");

        var setOverall = Assert.IsType<BudgetCommand.SetOverall>(result);
        Assert.Equal("500", setOverall.AmountText);
    }

    [Fact]
    public void Parse_riconosce_categoria_e_importo()
    {
        var result = BudgetCommandParser.Parse("spesa 200");

        var setCategory = Assert.IsType<BudgetCommand.SetCategory>(result);
        Assert.Equal("spesa", setCategory.CategoryText);
        Assert.Equal("200", setCategory.AmountText);
    }

    [Fact]
    public void Parse_gestisce_categorie_con_piu_parole()
    {
        var result = BudgetCommandParser.Parse("spesa alimentare 200,50");

        var setCategory = Assert.IsType<BudgetCommand.SetCategory>(result);
        Assert.Equal("spesa alimentare", setCategory.CategoryText);
        Assert.Equal("200,50", setCategory.AmountText);
    }

    [Fact]
    public void Parse_ritorna_null_per_testo_senza_importo()
    {
        var result = BudgetCommandParser.Parse("qualcosa senza numero");

        Assert.Null(result);
    }
}
