using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductFileConfiguration : IEntityTypeConfiguration<CatalogProductFile>
{
    public void Configure(EntityTypeBuilder<CatalogProductFile> builder)
    {
        builder.ToTable("catalog_product_files", "catalog");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.TenantId)
            .IsRequired();

        builder.Property(f => f.ProductId)
            .IsRequired();

        builder.Property(f => f.StorageKey)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(f => f.FileName)
            .HasMaxLength(260)
            .IsRequired();

        builder.Property(f => f.MimeType)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(f => f.SizeBytes)
            .IsRequired();

        builder.Property(f => f.Visibility)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.HasIndex(f => new { f.TenantId, f.ProductId });

        builder.HasIndex(f => new { f.TenantId, f.StorageKey })
            .IsUnique();
    }
}
