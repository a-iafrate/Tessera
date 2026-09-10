using Tessera.Core.Notifications;

namespace Tessera.Web.Services;

// Owns the in-memory aggregation buffers NotificationService writes to and
// NotificationAggregationFlushJob drains (docs/13-piano-miglioramenti.md, C3). Singleton,
// unlike the scoped NotificationService — the buffered state has to survive across the many
// scoped instances created over the life of the app, the same reason WebChannel's mailbox is a
// singleton too.
public sealed class NotificationAggregator
{
    public NotificationAggregationBuffer<ShoppingItemFact> ShoppingItemAdded { get; } = new();
    public NotificationAggregationBuffer<ShoppingItemFact> ShoppingItemChecked { get; } = new();
    public NotificationAggregationBuffer<ExpenseFact> ExpenseRecorded { get; } = new();
}
