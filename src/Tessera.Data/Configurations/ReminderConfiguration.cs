using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Reminders;

namespace Tessera.Data.Configurations;

public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Text).IsRequired();
        builder.Property(x => x.TimeZoneId).IsRequired();

        builder.OwnsOne(x => x.Recurrence);

        // Due reminders to notify: the query the scheduled worker runs most often
        // (docs/02-modello-dati.md) — a later checklist item, but the index belongs here.
        builder.HasIndex(x => new { x.IsCompleted, x.DueAt })
            .HasFilter("[IsCompleted] = 0");
    }
}
