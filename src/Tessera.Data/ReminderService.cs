using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Reminders;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class ReminderService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<Reminder> CreateOnceAsync(
        Guid spaceId, Guid userId, string text, DateTimeOffset dueAt, string timeZoneId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var reminder = new Reminder
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Text = text,
            DueAt = dueAt,
            TimeZoneId = timeZoneId,
            CreatedByUserId = userId,
        };
        db.Reminders.Add(reminder);
        await db.SaveChangesAsync(ct);
        return reminder;
    }

    public async Task<Reminder> CreateRecurringAsync(
        Guid spaceId, Guid userId, string text, DateTimeOffset firstDueAt, string timeZoneId,
        RecurrenceFrequency frequency, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var reminder = new Reminder
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Text = text,
            DueAt = firstDueAt,
            TimeZoneId = timeZoneId,
            CreatedByUserId = userId,
            Recurrence = new RecurrenceRule { Frequency = frequency },
        };
        db.Reminders.Add(reminder);
        await db.SaveChangesAsync(ct);
        return reminder;
    }

    public async Task<IReadOnlyList<Reminder>> GetPendingAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);

        return await db.Reminders
            .Where(x => x.SpaceId == spaceId && !x.IsCompleted)
            .OrderBy(x => x.DueAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Reminder?> CompleteAsync(Guid spaceId, Guid userId, Guid reminderId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var reminder = await db.Reminders
            .FirstOrDefaultAsync(x => x.Id == reminderId && x.SpaceId == spaceId && !x.IsCompleted, ct);
        if (reminder is null)
        {
            return null;
        }

        reminder.IsCompleted = true;
        reminder.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return reminder;
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.Reminders, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to Reminders in space {spaceId}.");
        }
    }
}
