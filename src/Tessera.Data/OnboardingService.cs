using Microsoft.EntityFrameworkCore;
using Tessera.Core.Onboarding;

namespace Tessera.Data;

// Onboarding progression (docs/10-conversazione.md): one novelty at a time. Each discovery
// hint retires once shown a few times, dismissed, or once the user has already used that
// feature — whichever comes first — so a bot that keeps explaining itself doesn't become
// noise. The sharing prompt is separate: it fires exactly once, regardless of which button
// (if any) the user taps.
public sealed class OnboardingService(TesseraDbContext db)
{
    private const int MaxShownCount = 3;

    // Order matters: this is also the priority when more than one hint is still eligible.
    private static readonly string[] HintOrder = ["shopping", "expenses", "reminders", "notes"];

    public async Task<int> RecordUsefulActionAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(u => u.Id == userId, ct);
        user.UsefulActionCount++;
        await db.SaveChangesAsync(ct);
        return user.UsefulActionCount;
    }

    public async Task<bool> TryShowSharingPromptOnceAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(u => u.Id == userId, ct);
        if (user.SharingPromptShown)
        {
            return false;
        }

        user.SharingPromptShown = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Never suggests the feature the user just used — that would be pointing at something
    // they already know works. Returns the hint key (not localized text): MessageProcessor
    // owns the resx mapping, keeping IStringLocalizer out of Tessera.Data.
    public async Task<string?> NextDiscoveryHintKeyAsync(Guid userId, string justUsedFeatureKey, CancellationToken ct)
    {
        foreach (var key in HintOrder)
        {
            if (key == justUsedFeatureKey || await HasUsedFeatureAsync(userId, key, ct))
            {
                continue;
            }

            var hint = await db.OnboardingHints.FirstOrDefaultAsync(h => h.UserId == userId && h.HintKey == key, ct);
            if (hint is null)
            {
                hint = new OnboardingHint { UserId = userId, HintKey = key };
                db.OnboardingHints.Add(hint);
            }
            else if (hint.Dismissed || hint.ShownCount >= MaxShownCount)
            {
                continue;
            }

            hint.ShownCount++;
            await db.SaveChangesAsync(ct);
            return key;
        }

        return null;
    }

    private async Task<bool> HasUsedFeatureAsync(Guid userId, string featureKey, CancellationToken ct) => featureKey switch
    {
        "shopping" => await db.ShoppingItems.AnyAsync(i => i.AddedByUserId == userId, ct),
        "expenses" => await db.Expenses.AnyAsync(e => e.CreatedByUserId == userId, ct),
        "reminders" => await db.Reminders.AnyAsync(r => r.CreatedByUserId == userId, ct),
        "notes" => await db.Notes.AnyAsync(n => n.CreatedByUserId == userId, ct),
        _ => true,
    };
}
