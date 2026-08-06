using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// Proactive counterpart to the /remind command's own reply — fires when DueAt arrives even
// if nobody is chatting with the bot at that moment (docs/01-architettura.md).
public sealed class RemindersDueJob(
    IServiceScopeFactory scopeFactory,
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    ILogger<RemindersDueJob> logger) : IScheduledJob
{
    public string Name => "RemindersDue";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        var now = DateTimeOffset.UtcNow;
        var due = await reminders.GetDueForNotificationAsync(now, ct);

        foreach (var reminder in due)
        {
            var recipientUserId = reminder.AssignedToUserId ?? reminder.CreatedByUserId;

            // No FK from CreatedByUserId/AssignedToUserId to User (docs/02-modello-dati.md) —
            // the account may have been deleted since. Nobody to notify; still record it so
            // this row doesn't come up again on the next tick.
            var recipient = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == recipientUserId, ct);
            if (recipient is null)
            {
                await reminders.RecordNotificationAsync(reminder.Id, now, ct);
                continue;
            }

            var culture = new CultureInfo(recipient.PreferredCulture);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var recipientIdentities = await identities.GetForUserAsync(recipientUserId, ct);
            var choices = new[] { new Choice(reminder.Text, $"remind.complete:{reminder.Id}") };

            foreach (var identity in recipientIdentities)
            {
                if (identity.ChannelName != channel.Name || identity.ExternalChatId is not { } chatId)
                {
                    continue;
                }

                try
                {
                    await channel.SendChoicesAsync(
                        new ChannelAddress(identity.ChannelName, chatId), localizer["Reminders.DueNotification"], choices, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send due reminder {ReminderId} to {ChannelName}/{ChatId}",
                        reminder.Id, identity.ChannelName, chatId);
                }
            }

            await reminders.RecordNotificationAsync(reminder.Id, now, ct);
        }
    }
}
