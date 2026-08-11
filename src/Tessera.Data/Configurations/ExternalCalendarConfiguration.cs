using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Calendars;

namespace Tessera.Data.Configurations;

public sealed class ExternalCalendarConfiguration : IEntityTypeConfiguration<ExternalCalendar>
{
    public void Configure(EntityTypeBuilder<ExternalCalendar> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderCalendarId).IsRequired();
        builder.Property(x => x.Name).IsRequired();

        // A refreshed calendarList must update the same row, not create a duplicate
        // (docs/02-modello-dati.md, docs/03-integrazioni.md).
        builder.HasIndex(x => new { x.LinkedAccountId, x.ProviderCalendarId }).IsUnique();
    }
}
