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

        // The exact lookup CalendarReminderJob/CalendarToListSuggestionJob does on every tick,
        // for every (space, member, event) triple in their respective lead-time windows. Kind
        // is part of the key so the same event can independently have a Reminder row and a
        // ListSuggestion row.
        builder.HasIndex(x => new { x.SpaceId, x.UserId, x.EventKey, x.EventStart, x.Kind }).IsUnique();
    }
}
