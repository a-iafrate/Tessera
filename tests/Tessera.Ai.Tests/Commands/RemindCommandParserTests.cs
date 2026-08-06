using Tessera.Ai.Commands;
using Tessera.Core.Reminders;

namespace Tessera.Ai.Tests.Commands;

public class RemindCommandParserTests
{
    [Fact]
    public void Parse_ritorna_ListPending_per_testo_vuoto()
    {
        var result = RemindCommandParser.Parse("");

        Assert.IsType<RemindCommand.ListPending>(result);
    }

    [Fact]
    public void Parse_ritorna_ListPending_per_solo_spazi()
    {
        var result = RemindCommandParser.Parse("   ");

        Assert.IsType<RemindCommand.ListPending>(result);
    }

    [Fact]
    public void Parse_data_e_ora_esplicite()
    {
        var result = RemindCommandParser.Parse("15/09 18:00 compra il pane");

        var once = Assert.IsType<RemindCommand.CreateOnce>(result);
        Assert.Equal(new DateOnly(DateTime.UtcNow.Year, 9, 15), once.Date);
        Assert.Equal(new TimeOnly(18, 0), once.Time);
        Assert.Equal("compra il pane", once.Text);
    }

    [Fact]
    public void Parse_data_con_anno_esplicito()
    {
        var result = RemindCommandParser.Parse("15/09/2027 18:00 compra il pane");

        var once = Assert.IsType<RemindCommand.CreateOnce>(result);
        Assert.Equal(new DateOnly(2027, 9, 15), once.Date);
    }

    [Fact]
    public void Parse_data_senza_ora()
    {
        var result = RemindCommandParser.Parse("15/09 compra il pane");

        var once = Assert.IsType<RemindCommand.CreateOnce>(result);
        Assert.Equal(new DateOnly(DateTime.UtcNow.Year, 9, 15), once.Date);
        Assert.Null(once.Time);
        Assert.Equal("compra il pane", once.Text);
    }

    [Theory]
    [InlineData("daily portare a spasso il cane", RecurrenceFrequency.Daily)]
    [InlineData("ogni giorno portare a spasso il cane", RecurrenceFrequency.Daily)]
    [InlineData("weekly buttare la spazzatura", RecurrenceFrequency.Weekly)]
    [InlineData("ogni settimana buttare la spazzatura", RecurrenceFrequency.Weekly)]
    [InlineData("monthly pagare l'affitto", RecurrenceFrequency.Monthly)]
    [InlineData("ogni mese pagare l'affitto", RecurrenceFrequency.Monthly)]
    public void Parse_frequenze_semplici(string input, RecurrenceFrequency expectedFrequency)
    {
        var result = RemindCommandParser.Parse(input);

        var recurring = Assert.IsType<RemindCommand.CreateRecurring>(result);
        Assert.Equal(expectedFrequency, recurring.Frequency);
    }

    [Theory]
    [InlineData("32/09 18:00 testo")]  // giorno non valido
    [InlineData("15/13 18:00 testo")]  // mese non valido
    [InlineData("15/09 25:00 testo")]  // ora non valida
    [InlineData("15/09 18:99 testo")]  // minuti non validi
    public void Parse_ritorna_null_per_date_o_orari_non_validi(string input)
    {
        var result = RemindCommandParser.Parse(input);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("ricordami di comprare il latte domani")] // linguaggio naturale — va a L3
    [InlineData("fra due settimane pagare le tasse")]
    [InlineData("solo testo senza data")]
    public void Parse_ritorna_null_per_linguaggio_naturale(string input)
    {
        var result = RemindCommandParser.Parse(input);

        Assert.Null(result);
    }
}
