using Tessera.Ai.Commands;
using Tessera.Core.Reminders;

namespace Tessera.Ai.Tests.Commands;

public class RecurringExpenseCommandParserTests
{
    [Fact]
    public void Parse_ritorna_ListActive_per_testo_vuoto()
    {
        var result = RecurringExpenseCommandParser.Parse("");

        Assert.IsType<RecurringExpenseCommand.ListActive>(result);
    }

    [Fact]
    public void Parse_crea_spesa_ricorrente_con_auto_registrazione()
    {
        var result = RecurringExpenseCommandParser.Parse("monthly 50 affitto");

        var create = Assert.IsType<RecurringExpenseCommand.Create>(result);
        Assert.Equal(RecurrenceFrequency.Monthly, create.Frequency);
        Assert.True(create.AutoRegister);
        Assert.Equal("50", create.AmountText);
        Assert.Equal("affitto", create.Description);
    }

    [Theory]
    [InlineData("monthly reminder 50 bolletta luce")]
    [InlineData("mensile promemoria 50 bolletta luce")]
    public void Parse_crea_spesa_ricorrente_solo_promemoria(string input)
    {
        var result = RecurringExpenseCommandParser.Parse(input);

        var create = Assert.IsType<RecurringExpenseCommand.Create>(result);
        Assert.False(create.AutoRegister);
        Assert.Equal("50", create.AmountText);
        Assert.Equal("bolletta luce", create.Description);
    }

    [Fact]
    public void Parse_riconosce_alias_italiani_di_frequenza()
    {
        var result = RecurringExpenseCommandParser.Parse("ogni settimana 20 pulizie");

        var create = Assert.IsType<RecurringExpenseCommand.Create>(result);
        Assert.Equal(RecurrenceFrequency.Weekly, create.Frequency);
    }

    [Theory]
    [InlineData("50 affitto")]              // manca la frequenza
    [InlineData("monthly affitto")]         // manca l'importo
    [InlineData("qualcosa senza struttura")]
    public void Parse_ritorna_null_per_forme_non_valide(string input)
    {
        var result = RecurringExpenseCommandParser.Parse(input);

        Assert.Null(result);
    }
}
