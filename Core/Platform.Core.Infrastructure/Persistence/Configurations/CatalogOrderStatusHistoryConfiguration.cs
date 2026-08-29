using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderStatusHistoryConfiguration
    : IEntityTypeConfiguration<CatalogOrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<CatalogOrderStatusHistory> builder)
    {
        builder.ToTable("catalog_order_status_history", "catalog");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Id)
            .ValueGeneratedNever();

        builder.Property(h => h.TenantId)
            .IsRequired();

        builder.Property(h => h.OrderId)
            .IsRequired();

        builder.Property(h => h.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(h => h.ActorType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(h => h.Reason)
            .HasMaxLength(1000);

        builder.Property(h => h.CreatedAt)
            .IsRequired();

        builder.HasIndex(h => new { h.TenantId, h.OrderId, h.CreatedAt });
    }
}
