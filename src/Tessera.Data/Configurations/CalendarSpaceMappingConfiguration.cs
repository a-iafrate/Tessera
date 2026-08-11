using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Calendars;

namespace Tessera.Data.Configurations;

public sealed class CalendarSpaceMappingConfiguration : IEntityTypeConfiguration<CalendarSpaceMapping>
{
    public void Configure(EntityTypeBuilder<CalendarSpaceMapping> builder)
    {
        // A calendar maps into a given space at most once — the natural composite key
        // (docs/02-modello-dati.md).
        builder.HasKey(x => new { x.ExternalCalendarId, x.SpaceId });
    }
}
