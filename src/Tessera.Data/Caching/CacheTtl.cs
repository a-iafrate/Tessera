namespace Tessera.Data.Caching;

// The TTL table from docs/05-ottimizzazioni.md, "Cache: cosa e per quanto" — kept in one place
// so a change to the table means editing here, not hunting through every call site.
internal static class CacheTtl
{
    public static readonly TimeSpan ChannelIdentity = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MembershipPermissions = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Categories = TimeSpan.FromHours(1);

    // No entry here for the per-space tool schema docs/05-ottimizzazioni.md's table lists at
    // 1h: LlmTools.Build's output depends on the calling *member's* permissions, not just the
    // space, so a cache keyed by space alone would leak a Write-level tool set to a Read-only
    // member sharing that space within the TTL window. Building the filtered list from the
    // (already-cached) membership permissions is cheap enough — a handful of dictionary
    // lookups, no serialization, no LLM call — that skipping this second cache layer costs
    // nothing measurable while keeping the result correct.

    // Not a fixed span: the OAuth access token cache expires at (provider-issued expiry − 5
    // minutes), computed by the caller from LinkedAccount.TokenExpiresAt
    // (docs/07-compliance.md, "Cache in memoria, con attenzione").
    public static readonly TimeSpan AccessTokenSafetyMargin = TimeSpan.FromMinutes(5);
}
