using Tessera.Core.Notifications;

namespace Tessera.Core.Tests.Notifications;

public class NotificationAggregationBufferTests
{
    private static readonly DateTimeOffset Opened = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly NotificationWindowKey Key = new(Guid.NewGuid(), Guid.NewGuid(), "ShoppingItemAdded");

    [Fact]
    public void TakeReady_ReturnsNothing_BeforeTheWindowElapses()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);

        var ready = buffer.TakeReady(Opened.AddSeconds(59));

        Assert.Empty(ready);
    }

    [Fact]
    public void TakeReady_ReturnsTheWindow_ExactlyAtTheDurationBoundary()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);

        var ready = buffer.TakeReady(Opened.AddSeconds(60));

        var (key, events) = Assert.Single(ready);
        Assert.Equal(Key, key);
        Assert.Equal(["milk"], events);
    }

    [Fact]
    public void Add_JoinsTheSameWindow_WhenCalledRepeatedlyForTheSameKey()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);
        buffer.Add(Key, "eggs", TimeSpan.FromSeconds(60), Opened.AddSeconds(10));
        buffer.Add(Key, "bread", TimeSpan.FromSeconds(60), Opened.AddSeconds(20));

        var ready = buffer.TakeReady(Opened.AddSeconds(60));

        var (_, events) = Assert.Single(ready);
        Assert.Equal(["milk", "eggs", "bread"], events);
    }

    [Fact]
    public void Add_KeepsTheWindowsOpenDuration_FromTheFirstCallOnly()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);
        // A later Add for the same key passing a longer duration doesn't re-extend the window —
        // its lifetime is fixed at open time.
        buffer.Add(Key, "eggs", TimeSpan.FromMinutes(5), Opened.AddSeconds(10));

        var ready = buffer.TakeReady(Opened.AddSeconds(60));

        Assert.Single(ready);
    }

    [Fact]
    public void TakeReady_KeepsDifferentKeysIndependent()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        var otherKey = Key with { EventType = "ShoppingItemChecked" };
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);
        buffer.Add(otherKey, "eggs", TimeSpan.FromMinutes(5), Opened);

        var ready = buffer.TakeReady(Opened.AddSeconds(60));

        var (key, events) = Assert.Single(ready);
        Assert.Equal(Key, key);
        Assert.Equal(["milk"], events);
    }

    [Fact]
    public void TakeReady_RemovesFlushedWindows_SoTheyDoNotFlushAgain()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);
        buffer.TakeReady(Opened.AddSeconds(60));

        var ready = buffer.TakeReady(Opened.AddSeconds(120));

        Assert.Empty(ready);
    }

    [Fact]
    public void Add_AfterAFlush_OpensAFreshWindowForTheSameKey()
    {
        var buffer = new NotificationAggregationBuffer<string>();
        buffer.Add(Key, "milk", TimeSpan.FromSeconds(60), Opened);
        buffer.TakeReady(Opened.AddSeconds(60));

        buffer.Add(Key, "eggs", TimeSpan.FromSeconds(60), Opened.AddSeconds(70));
        var tooEarly = buffer.TakeReady(Opened.AddSeconds(100));
        var ready = buffer.TakeReady(Opened.AddSeconds(130));

        Assert.Empty(tooEarly);
        var (_, events) = Assert.Single(ready);
        Assert.Equal(["eggs"], events);
    }
}
