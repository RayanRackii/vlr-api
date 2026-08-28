using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductConfiguration : IEntityTypeConfiguration<CatalogProduct>
{
    public void Configure(EntityTypeBuilder<CatalogProduct> builder)
    {
        builder.ToTable("catalog_products", "catalog");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(p => p.Code)
            .HasMaxLength(80);

        builder.Property(p => p.Description)
            .HasColumnType("text");

        builder.Property(p => p.Price)
            .HasColumnType("decimal(18,2)");

        builder.Property(p => p.Currency)
            .HasMaxLength(3)
            .HasDefaultValue("BRL")
            .IsRequired();

        builder.Property(p => p.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.Code })
            .IsUnique()
            .HasFilter("code IS NOT NULL");

        builder.HasIndex(p => new { p.TenantId, p.Name });

        builder.HasIndex(p => new { p.TenantId, p.IsActive });

        builder.HasMany(p => p.Files)
            .WithOne(f => f.Product)
            .HasForeignKey(f => f.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Files)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
