using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderItemConfiguration : IEntityTypeConfiguration<CatalogOrderItem>
{
    public void Configure(EntityTypeBuilder<CatalogOrderItem> builder)
    {
        builder.ToTable("catalog_order_items", "catalog");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .ValueGeneratedNever();

        builder.Property(i => i.TenantId)
            .IsRequired();

        builder.Property(i => i.OrderId)
            .IsRequired();

        builder.Property(i => i.ProductId)
            .IsRequired();

        builder.Property(i => i.ProductNameSnapshot)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(i => i.ProductCodeSnapshot)
            .HasMaxLength(80);

        builder.Property(i => i.UnitPriceSnapshot)
            .HasColumnType("decimal(18,2)");

        builder.Property(i => i.Currency)
            .HasMaxLength(3)
            .HasDefaultValue("BRL")
            .IsRequired();

        builder.Property(i => i.Quantity)
            .IsRequired();

        builder.Property(i => i.SubTotal)
            .HasColumnType("decimal(18,2)");

        builder.Property(i => i.CreatedAt)
            .IsRequired();

        builder.HasIndex(i => new { i.TenantId, i.OrderId });

        builder.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
