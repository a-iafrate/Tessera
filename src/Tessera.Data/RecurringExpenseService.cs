using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Expenses;
using Tessera.Core.Reminders;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class RecurringExpenseService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<RecurringExpense> CreateAsync(
        Guid spaceId, Guid userId, decimal amount, string description,
        RecurrenceFrequency frequency, bool autoRegister, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        var recurring = new RecurringExpense
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Amount = amount,
            Currency = space.Currency,
            Description = description,
            Recurrence = new RecurrenceRule { Frequency = frequency },
            AutoRegister = autoRegister,
        };
        db.RecurringExpenses.Add(recurring);
        await db.SaveChangesAsync(ct);
        return recurring;
    }

    public async Task<IReadOnlyList<RecurringExpense>> GetActiveAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.RecurringExpenses
            .Where(x => x.SpaceId == spaceId && (x.EndsOn == null || x.EndsOn >= today))
            .OrderBy(x => x.Description)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // System sweep across every space for the generation job — there's no acting user to
    // check access against, unlike the methods above (docs/01-architettura.md).
    public async Task<IReadOnlyList<RecurringExpense>> GetAllActiveAsync(CancellationToken ct) =>
        await db.RecurringExpenses.AsNoTracking().ToListAsync(ct);

    // "Due" per the simplified recurrence model (RecurrenceRule's own doc comment): compares
    // LastGeneratedFor against the current period rather than "does a similar expense
    // already exist" (docs/02-modello-dati.md).
    public static bool IsDue(RecurringExpense recurring, DateOnly today) => recurring.LastGeneratedFor switch
    {
        null => true,
        { } last when recurring.Recurrence.Frequency == RecurrenceFrequency.Daily => last != today,
        { } last when recurring.Recurrence.Frequency == RecurrenceFrequency.Weekly => today >= last.AddDays(7),
        { } last when recurring.Recurrence.Frequency == RecurrenceFrequency.Monthly =>
            last.Year != today.Year || last.Month != today.Month,
        { } last when recurring.Recurrence.Frequency == RecurrenceFrequency.Yearly => last.Year != today.Year,
        _ => false,
    };

    // AutoRegister creates the Expense (Note carries the recurring rule's description,
    // since Expense has no separate description field); otherwise this only advances
    // LastGeneratedFor and the caller sends a reminder instead (docs/02-modello-dati.md).
    public async Task<RecurringExpense?> GenerateAsync(Guid recurringExpenseId, DateOnly today, CancellationToken ct)
    {
        var recurring = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == recurringExpenseId, ct);
        if (recurring is null)
        {
            return null;
        }

        if (recurring.AutoRegister)
        {
            var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == recurring.SpaceId, ct);
            db.Expenses.Add(new Expense
            {
                Id = Guid.NewGuid(),
                SpaceId = recurring.SpaceId,
                Amount = recurring.Amount,
                Currency = recurring.Currency,
                CategoryId = recurring.CategoryId,
                Date = today,
                Note = recurring.Description,
                CreatedByUserId = space.OwnerId,
            });
        }

        recurring.LastGeneratedFor = today;
        await db.SaveChangesAsync(ct);
        return recurring;
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.Expenses, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to Expenses in space {spaceId}.");
        }
    }
}
