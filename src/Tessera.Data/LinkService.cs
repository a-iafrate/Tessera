using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Users;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

public sealed class LinkService(TesseraDbContext db)
{
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

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
