using Tessera.Core.Conversations;

namespace Tessera.Core.Tests.Conversations;

public class RecentExchangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ReturnsEmpty_ForNullOrEmptyJson()
    {
        Assert.Empty(RecentExchange.Parse(null, Now));
        Assert.Empty(RecentExchange.Parse("", Now));
    }

    [Fact]
    public void Parse_ReturnsEmpty_ForMalformedJson()
    {
        var result = RecentExchange.Parse("{not valid json", Now);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ExcludesEntriesOlderThanThirtyMinutes()
    {
        var fresh = new RecentExchange("quanto ho speso a gennaio", "query_expense_history", "{}", Now.AddMinutes(-10));
        var stale = new RecentExchange("aggiungi il latte", "add_shopping_item", "{}", Now.AddMinutes(-31));
        var json = RecentExchange.Serialize([stale], fresh);

        var result = RecentExchange.Parse(json, Now);

        Assert.Single(result);
        Assert.Equal(fresh.UserText, result[0].UserText);
    }

    [Fact]
    public void Parse_KeepsEntryExactlyAtTheThirtyMinuteBoundary()
    {
        var atBoundary = new RecentExchange("e a febbraio?", "query_expense_history", "{}", Now.AddMinutes(-30));
        var json = RecentExchange.Serialize([], atBoundary);

        var result = RecentExchange.Parse(json, Now);

        Assert.Single(result);
    }

    [Fact]
    public void Serialize_KeepsOnlyTheLastFourEntries()
    {
        var existing = Enumerable.Range(1, 4)
            .Select(i => new RecentExchange($"message {i}", null, null, Now.AddMinutes(-i)))
            .ToList();
        var newEntry = new RecentExchange("message 5", null, null, Now);

        var json = RecentExchange.Serialize(existing, newEntry);
        var result = RecentExchange.Parse(json, Now);

        Assert.Equal(4, result.Count);
        Assert.Equal("message 2", result[0].UserText);
        Assert.Equal("message 5", result[^1].UserText);
    }

    [Fact]
    public void Serialize_RoundTripsToolNameAndArguments()
    {
        var entry = new RecentExchange(
            "quanto ho speso a gennaio",
            "query_expense_history",
            """{"date_from":"2026-01-01","date_to":"2026-01-31","aggregation":"total"}""",
            Now);

        var json = RecentExchange.Serialize([], entry);
        var result = RecentExchange.Parse(json, Now);

        Assert.Equal(entry, Assert.Single(result));
    }

    [Fact]
    public void Serialize_RoundTripsAPlainTextReplyWithNoToolCalled()
    {
        var entry = new RecentExchange("grazie!", null, null, Now);

        var json = RecentExchange.Serialize([], entry);
        var result = RecentExchange.Parse(json, Now);

        Assert.Equal(entry, Assert.Single(result));
    }
}
