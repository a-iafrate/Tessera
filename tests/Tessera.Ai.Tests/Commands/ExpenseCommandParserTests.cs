using Tessera.Ai.Commands;

namespace Tessera.Ai.Tests.Commands;

public class ExpenseCommandParserTests
{
    [Fact]
    public void Parse_riconosce_importo_senza_categoria()
    {
        var result = ExpenseCommandParser.Parse("20");

        Assert.NotNull(result);
        Assert.Equal("20", result.AmountText);
        Assert.Null(result.CategoryText);
    }

    [Fact]
    public void Parse_riconosce_importo_e_categoria()
    {
        var result = ExpenseCommandParser.Parse("20 spesa alimentare");

        Assert.NotNull(result);
        Assert.Equal("20", result.AmountText);
        Assert.Equal("spesa alimentare", result.CategoryText);
    }

    [Fact]
    public void Parse_accetta_importo_con_decimali()
    {
        var result = ExpenseCommandParser.Parse("12,50 trasporti");

        Assert.NotNull(result);
        Assert.Equal("12,50", result.AmountText);
        Assert.Equal("trasporti", result.CategoryText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("spesa alimentare")]
    public void Parse_ritorna_null_senza_importo(string input)
    {
        var result = ExpenseCommandParser.Parse(input);

        Assert.Null(result);
    }
}
