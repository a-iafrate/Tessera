namespace Tessera.Core.Expenses;

// Tuning for the warranty-reminder proposal (docs/13-piano-miglioramenti.md, E2) — not per-plan
// or per-space data, a placeholder like BillingDefaults.TrialDays: adjustable without a schema
// change since nothing stores it per row.
public static class WarrantyReminderDefaults
{
    // A receipt line at or above this is treated as a "bigger purchase" worth a warranty
    // reminder — currency-naive (compared directly against ExpenseLine.Price), which is fine
    // for a heuristic trigger on an app whose spaces are overwhelmingly EUR-denominated today.
    public const decimal ThresholdAmount = 100m;

    // Just under the common 24-month EU statutory warranty, so the reminder lands before it
    // actually expires rather than on the exact day.
    public const int MonthsAhead = 23;
}
