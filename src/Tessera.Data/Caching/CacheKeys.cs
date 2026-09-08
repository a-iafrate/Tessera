namespace Tessera.Data.Caching;

// Shared cache key builders — kept in one place so a repository's read side and a mutator's
// invalidation call (SpaceService, InviteService) can never drift out of sync on the key shape
// (docs/05-ottimizzazioni.md, "Cache: cosa e per quanto").
internal static class CacheKeys
{
    public static string ChannelIdentity(string channelName, string externalUserId) =>
        $"channel-identity:{channelName}:{externalUserId}";

    public static string Membership(Guid userId, Guid spaceId) =>
        $"membership:{userId}:{spaceId}";

    public static string Categories(Guid spaceId) =>
        $"categories:{spaceId}";

    public static string AccessToken(Guid linkedAccountId) =>
        $"access-token:{linkedAccountId}";
}
