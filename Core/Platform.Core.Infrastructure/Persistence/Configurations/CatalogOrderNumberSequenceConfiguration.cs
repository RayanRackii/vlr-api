using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderNumberSequenceConfiguration
    : IEntityTypeConfiguration<CatalogOrderNumberSequence>
{
    public void Configure(EntityTypeBuilder<CatalogOrderNumberSequence> builder)
    {
        builder.ToTable("catalog_order_number_sequences", "catalog");

        builder.HasKey(s => s.TenantId);

        builder.Property(s => s.TenantId)
            .ValueGeneratedNever();

        builder.Property(s => s.LastNumber)
            .IsRequired();
    }
}
