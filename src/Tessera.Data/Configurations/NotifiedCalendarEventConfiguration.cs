using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Calendars;

namespace Tessera.Data.Configurations;

public sealed class NotifiedCalendarEventConfiguration : IEntityTypeConfiguration<NotifiedCalendarEvent>
{
    public void Configure(EntityTypeBuilder<NotifiedCalendarEvent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventKey).IsRequired();

        // The exact lookup CalendarReminderJob does on every tick, for every (space, member,
        // event) triple in the lead-time window.
        builder.HasIndex(x => new { x.SpaceId, x.UserId, x.EventKey, x.EventStart }).IsUnique();
    }
}
