using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Users;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

public sealed class LinkService(TesseraDbContext db)
{
    // "Linked bots" for a Space (docs/02-modello-dati.md, docs/04-costi.md) has no direct
    // column to read: ChannelIdentity is per-User, not per-Space (one Telegram account can
    // belong to several spaces via Membership), and a linked Telegram group is a completely
    // separate mechanism (Space.GroupChatId). The count is therefore derived: members of this
    // space who have linked at least one identity, plus one more if a group is linked. Web
    // chat is excluded on purpose — it's the console's own always-available entry point
    // (docs/06-roadmap.md), not an extra bot connection to gate against the plan.
    public async Task<int> GetLinkedBotCountAsync(Guid spaceId, CancellationToken ct)
    {
        var memberUserIds = await db.Memberships
            .Where(x => x.SpaceId == spaceId)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        var linkedMemberCount = await db.ChannelIdentities
            .Where(x => memberUserIds.Contains(x.UserId) && x.ChannelName != "web")
            .Select(x => x.UserId)
            .Distinct()
            .CountAsync(ct);

        var hasLinkedGroup = await db.Spaces
            .Where(x => x.Id == spaceId)
            .Select(x => x.GroupChatId != null)
            .FirstAsync(ct);

        return linkedMemberCount + (hasLinkedGroup ? 1 : 0);
    }

    // Only enforced at the one clean, unambiguous moment: linking a Telegram group to a space
    // (MessageProcessor). A member linking their own private Telegram isn't scoped to a single
    // space the same way — they may belong to several — so that path isn't gated here; see the
    // discussion in docs/06-roadmap.md.
    public async Task<bool> CanLinkAnotherBotAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);
        var current = await GetLinkedBotCountAsync(spaceId, ct);
        return current < plan.MaxLinkedBots;
    }

    public async Task<LinkToken> CreateTokenAsync(Guid userId, string channelName, CancellationToken ct)
    {
        var token = new LinkToken
        {
            Id = Guid.NewGuid(),
            Token = GenerateToken(),
            UserId = userId,
            ChannelName = channelName,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.LinkTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return token;
    }

    // Null covers all three failure cases (not found, already used, expired) — the caller
    // can't distinguish, and shouldn't: it just means "generate a new one from the console".
    public async Task<DomainUser?> ConsumeTokenAsync(
        string token, string channelName, string externalUserId, string externalChatId, CancellationToken ct)
    {
        var linkToken = await db.LinkTokens.FirstOrDefaultAsync(x => x.Token == token && x.ChannelName == channelName, ct);
        if (linkToken is null || linkToken.ConsumedAt is not null || linkToken.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        linkToken.ConsumedAt = DateTimeOffset.UtcNow;

        db.ChannelIdentities.Add(new ChannelIdentity
        {
            Id = Guid.NewGuid(),
            UserId = linkToken.UserId,
            ChannelName = channelName,
            ExternalUserId = externalUserId,
            ExternalChatId = externalChatId,
            LinkedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        return await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == linkToken.UserId, ct);
    }

    // Web chat has no linking flow — the user is already authenticated in console, so their
    // ChannelIdentity is provisioned automatically instead of via a token (docs/06-roadmap.md:
    // web chat channel). ExternalUserId and ExternalChatId are both just the user's own id:
    // one identity per user, no external chat_id to disambiguate.
    public async Task<ChannelIdentity> EnsureWebIdentityAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.ChannelIdentities
            .FirstOrDefaultAsync(x => x.ChannelName == "web" && x.ExternalUserId == userId.ToString(), ct);
        if (existing is not null)
        {
            return existing;
        }

        var identity = new ChannelIdentity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChannelName = "web",
            ExternalUserId = userId.ToString(),
            ExternalChatId = userId.ToString(),
            LinkedAt = DateTimeOffset.UtcNow,
        };
        db.ChannelIdentities.Add(identity);
        await db.SaveChangesAsync(ct);
        return identity;
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
