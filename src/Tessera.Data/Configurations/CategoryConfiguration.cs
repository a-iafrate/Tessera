using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(x => x.Id);

        // System categories are resource keys and localize; user categories are content
        // and never translate (docs/09-localizzazione.md) — the two never mix per row.
        builder.HasData(
            new Category { Id = SystemCategoryIds.Groceries, ResourceKey = "Category.Groceries" },
            new Category { Id = SystemCategoryIds.Transport, ResourceKey = "Category.Transport" },
            new Category { Id = SystemCategoryIds.Utilities, ResourceKey = "Category.Utilities" },
            new Category { Id = SystemCategoryIds.Entertainment, ResourceKey = "Category.Entertainment" },
            new Category { Id = SystemCategoryIds.Health, ResourceKey = "Category.Health" },
            new Category { Id = SystemCategoryIds.Other, ResourceKey = "Category.Other" });
    }
}
