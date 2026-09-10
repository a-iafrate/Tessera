using System.Collections.Concurrent;

namespace Tessera.Core.Notifications;

// In-memory aggregation window per (space, recipient, event type) — docs/13-piano-miglioramenti.md,
// C3: ten shopping-list additions in a row used to fan out as ten separate messages to every
// other space member; this batches same-window occurrences so they render as one. Pure and
// DB-free — `now` is always passed in rather than read from the clock — so the windowing
// decision itself is unit-testable without a database (CLAUDE.md).
//
// Best-effort like the rest of the notification pipeline (every send already swallows and logs
// its own failures): a window can, in a narrow race, be flushed by TakeReady at the exact
// instant a concurrent Add is still writing to it, losing that one occurrence. Acceptable here —
// nothing in this pipeline is transactional or guaranteed-delivery — and not worth a per-key
// lock to close for a single-instance, family-scale notification batcher.
public sealed class NotificationAggregationBuffer<TEvent>
{
    private sealed class Window
    {
        public required DateTimeOffset OpenedAt { get; init; }
        public required TimeSpan Duration { get; init; }
        public required List<TEvent> Events { get; init; }
    }

    private readonly ConcurrentDictionary<NotificationWindowKey, Window> windows = new();

    // The first call for a given key opens the window with `duration`; later calls join the
    // same window regardless of the duration they pass — a window's lifetime is fixed at open
    // time, deliberately, so it can't be kept re-extended indefinitely by a steady trickle of
    // events.
    public void Add(NotificationWindowKey key, TEvent evt, TimeSpan duration, DateTimeOffset now)
    {
        var window = windows.GetOrAdd(key, _ => new Window { OpenedAt = now, Duration = duration, Events = [] });
        lock (window.Events)
        {
            window.Events.Add(evt);
        }
    }

    // Removes and returns every window whose duration has elapsed by `now`.
    public IReadOnlyList<(NotificationWindowKey Key, IReadOnlyList<TEvent> Events)> TakeReady(DateTimeOffset now)
    {
        var ready = new List<(NotificationWindowKey, IReadOnlyList<TEvent>)>();
        foreach (var (key, window) in windows)
        {
            if (now - window.OpenedAt < window.Duration || !windows.TryRemove(key, out var removed))
            {
                continue;
            }

            lock (removed.Events)
            {
                ready.Add((key, removed.Events.ToList()));
            }
        }

        return ready;
    }
}
