using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Channels;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// "8 in the morning" is a different UTC instant per TimeZoneId, so this polls every 15
// minutes and fires for whoever's local clock matches their DigestHourLocal right now,
// rather than running once at one fixed hour (docs/01-architettura.md).
public sealed class DailyDigestJob(
    IServiceScopeFactory scopeFactory,
    IChannelRegistry channelRegistry,
    IConfiguration configuration,
    EmailUnsubscribeTokenService unsubscribeTokens,
    IStringLocalizer<Messages> localizer,
    ILogger<DailyDigestJob> logger) : IScheduledJob
{
    public string Name => "DailyDigest";

    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
        var expenses = scope.ServiceProvider.GetRequiredService<ExpenseService>();
        var spaces = scope.ServiceProvider.GetRequiredService<SpaceService>();
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        var now = DateTimeOffset.UtcNow;
        var candidates = await db.DomainUsers
            .Where(u => u.DefaultSpaceId != null && u.TimeZoneId != null)
            .ToListAsync(ct);

        foreach (var user in candidates)
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId!);
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var today = DateOnly.FromDateTime(localNow.Date);

            if (user.LastDigestSentFor == today || localNow.Hour != user.DigestHourLocal)
            {
                continue;
            }

            var culture = new CultureInfo(user.PreferredCulture);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            // Every space the user belongs to, not just DefaultSpaceId — someone with Home +
            // Personal + a group only ever saw a third of their day before this
            // (docs/13-piano-miglioramenti.md, E4). DigestService.BuildAsync no longer throws
            // for a space where this user's membership lacks Read on one of the four domains;
            // it just contributes nothing for that domain.
            var userSpaces = await spaces.GetForUserAsync(user.Id, ct);
            var perSpaceSections = new List<(string SpaceName, IReadOnlyList<(string Header, string Body)> Sections)>();
            foreach (var space in userSpaces)
            {
                var daily = await digest.BuildAsync(space.Id, user.Id, timeZone, today, ct);
                var currency = await expenses.GetSpaceCurrencyAsync(space.Id, ct);
                var categories = await expenses.GetCategoriesAsync(space.Id, ct);
                var spaceSections = DigestFormatter.BuildSections(daily, categories, currency, timeZone, culture, localizer);
                perSpaceSections.Add((space.Name, spaceSections));
            }

            var sections = DigestFormatter.CombineSpaces(perSpaceSections);
            var text = DigestFormatter.Format(sections, localizer);

            var userIdentities = await identities.GetForUserAsync(user.Id, ct);
            foreach (var identity in userIdentities)
            {
                if (channelRegistry.TryGet(identity.ChannelName) is not { } identityChannel
                    || identity.ExternalChatId is not { } chatId)
                {
                    continue;
                }

                try
                {
                    // Email needs a subject, styled sections and an unsubscribe link — none of
                    // which fit IChannel's generic "send this text" contract — so it's handled
                    // directly rather than through SendTextAsync (docs/13-piano-miglioramenti.md,
                    // C1). Gated on the explicit opt-in even though the identity, once
                    // provisioned, doesn't go away when the user turns it back off from Profile.
                    if (identityChannel is EmailChannel emailChannel)
                    {
                        if (!user.EmailDigestEnabled)
                        {
                            continue;
                        }

                        var unsubscribeUrl = $"{BaseUrl}/email/unsubscribe?token={Uri.EscapeDataString(unsubscribeTokens.CreateToken(user.Id))}";
                        await emailChannel.SendDigestAsync(
                            chatId, localizer["Email.Digest.Subject"], sections, unsubscribeUrl,
                            localizer["Email.Digest.UnsubscribeLinkText"], culture.TwoLetterISOLanguageName, ct);
                    }
                    else
                    {
                        await identityChannel.SendTextAsync(new ChannelAddress(identity.ChannelName, chatId), text, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send daily digest to {ChannelName}/{ChatId}", identity.ChannelName, chatId);
                }
            }

            user.LastDigestSentFor = today;
        }

        await db.SaveChangesAsync(ct);
    }

    private string BaseUrl => configuration["App:BaseUrl"]?.TrimEnd('/')
        ?? throw new InvalidOperationException("Configuration key 'App:BaseUrl' is required to build unsubscribe links.");
}
