using Tessera.Core.Reminders;

namespace Tessera.Core.Tests.Reminders;

public class RecurrenceRuleTests
{
    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 1)]
    [InlineData(RecurrenceFrequency.Weekly, 7)]
    public void Advance_aggiunge_il_numero_di_giorni_corretto(RecurrenceFrequency frequency, int expectedDays)
    {
        var from = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

        var next = RecurrenceRule.Advance(from, frequency);

        Assert.Equal(from.AddDays(expectedDays), next);
    }

    [Fact]
    public void Advance_mensile_avanza_di_un_mese()
    {
        var from = new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);

        var next = RecurrenceRule.Advance(from, RecurrenceFrequency.Monthly);

        Assert.Equal(from.AddMonths(1), next);
    }

    [Fact]
    public void Advance_annuale_avanza_di_un_anno()
    {
        var from = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

        var next = RecurrenceRule.Advance(from, RecurrenceFrequency.Yearly);

        Assert.Equal(from.AddYears(1), next);
    }
}
