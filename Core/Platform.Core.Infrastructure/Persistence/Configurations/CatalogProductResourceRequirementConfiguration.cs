using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductResourceRequirementConfiguration
    : IEntityTypeConfiguration<CatalogProductResourceRequirement>
{
    public void Configure(EntityTypeBuilder<CatalogProductResourceRequirement> builder)
    {
        builder.ToTable("product_resource_requirements", "catalog", table =>
        {
            // Exactly one of RentalAssetId / AssetCategoryId must be set.
            table.HasCheckConstraint(
                "ck_product_resource_requirements_exactly_one_selector",
                "(rental_asset_id IS NOT NULL AND asset_category_id IS NULL) " +
                "OR (rental_asset_id IS NULL AND asset_category_id IS NOT NULL)");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedNever();

        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.ProductId)
            .IsRequired();

        builder.Property(r => r.ResourceQuantity)
            .IsRequired();

        builder.Property(r => r.DurationMinutesPerUnit)
            .IsRequired();

        builder.Property(r => r.PrepMinutes)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(r => r.CleanupMinutes)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(r => r.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(r => r.Version)
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.ProductId, r.IsActive });

        builder.HasOne(r => r.Product)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Tenant-qualified composite FK: application checks alone must not permit a cross-tenant
        // RentalAsset association (nullable — AssetCategoryId may be set instead; the exactly-one
        // check constraint above already enforces the selector shape).
        builder.HasOne<RentalAsset>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RentalAssetId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
