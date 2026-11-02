using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderItemResourceRequirementConfiguration
    : IEntityTypeConfiguration<CatalogOrderItemResourceRequirement>
{
    public void Configure(EntityTypeBuilder<CatalogOrderItemResourceRequirement> builder)
    {
        builder.ToTable("order_item_resource_requirements", "catalog", table =>
        {
            // Exactly one of RentalAssetId / AssetCategoryId must be set, mirroring the source
            // product_resource_requirements shape the snapshot was captured from.
            table.HasCheckConstraint(
                "ck_order_item_resource_requirements_exactly_one_selector",
                "(rental_asset_id IS NOT NULL AND asset_category_id IS NULL) " +
                "OR (rental_asset_id IS NULL AND asset_category_id IS NOT NULL)");
        });

        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.Property(r => r.Id)
            .ValueGeneratedNever();

        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.OrderItemId)
            .IsRequired();

        builder.Property(r => r.ResourceQuantity)
            .IsRequired();

        builder.Property(r => r.DurationMinutesPerUnit)
            .IsRequired();

        builder.Property(r => r.PrepMinutes)
            .IsRequired();

        builder.Property(r => r.CleanupMinutes)
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        // Non-unique: Phase 0 snapshots one requirement per item; Phase 1 may add more lines.
        builder.HasIndex(r => new { r.TenantId, r.OrderItemId });

        builder.HasOne(r => r.OrderItem)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.OrderItemId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Restrictive: historical snapshots must survive even if the source requirement row
        // is ever hard-deleted (Phase 0 never deletes requirement rows, only deactivates).
        builder.HasOne(r => r.SourceRequirement)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.SourceRequirementId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Tenant-qualified composite FK: application checks alone must not permit a cross-tenant
        // RentalAsset association (nullable — AssetCategoryId may be set instead).
        builder.HasOne<RentalAsset>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RentalAssetId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
