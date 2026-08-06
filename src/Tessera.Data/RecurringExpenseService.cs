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
