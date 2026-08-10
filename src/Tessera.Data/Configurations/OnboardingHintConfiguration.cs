using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Onboarding;

namespace Tessera.Data.Configurations;

public sealed class OnboardingHintConfiguration : IEntityTypeConfiguration<OnboardingHint>
{
    public void Configure(EntityTypeBuilder<OnboardingHint> builder)
    {
        builder.HasKey(x => new { x.UserId, x.HintKey });
        builder.Property(x => x.HintKey).IsRequired();
    }
}
