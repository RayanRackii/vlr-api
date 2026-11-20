using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderItemResourceAllocationConfiguration
    : IEntityTypeConfiguration<CatalogOrderItemResourceAllocation>
{
    public void Configure(EntityTypeBuilder<CatalogOrderItemResourceAllocation> builder)
    {
        builder.ToTable("order_item_resource_allocations", "catalog");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.RequirementId)
            .IsRequired();

        // Nullable since slice API-5: a planning-only asset has no mirror reservation.
        builder.Property(a => a.AssetAllocationId);

        builder.Property(a => a.ReservationItemId);

        builder.Property(a => a.Sequence)
            .IsRequired();

        builder.Property(a => a.PlanningRevision)
            .IsRequired();

        builder.Property(a => a.RowVersion)
            .IsConcurrencyToken()
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(a => a.IsCurrent)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(a => a.AllocatedQuantity)
            .IsRequired();

        // Nullable since slice API-5: set only when the assigned asset is rental-backed.
        builder.Property(a => a.RentalAssetId);

        builder.Property(a => a.PlannedWorkStart)
            .IsRequired();

        builder.Property(a => a.PlannedWorkEnd)
            .IsRequired();

        builder.Property(a => a.TimeZoneId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(AllocationStatus.Scheduled)
            .IsRequired();

        builder.Property(a => a.Reason)
            .HasMaxLength(1000);

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.ReservationItemId })
            .IsUnique();

        // One catalog allocation per planning record; rows not linked yet (null) do not collide.
        builder.HasIndex(a => new { a.TenantId, a.AssetAllocationId })
            .IsUnique()
            .HasFilter("asset_allocation_id IS NOT NULL")
            .HasDatabaseName("ux_order_item_resource_allocations_tenant_asset_allocation");

        // Unique "current occurrence" per requirement+sequence; historical (superseded) rows
        // are excluded so Phase 1 reschedule can keep old rows without violating uniqueness.
        builder.HasIndex(a => new { a.TenantId, a.RequirementId, a.Sequence })
            .IsUnique()
            .HasFilter("is_current = true")
            .HasDatabaseName("ux_order_item_resource_allocations_current_occurrence");

        builder.HasOne(a => a.Requirement)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.RequirementId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ReservationItem)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ReservationItemId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Tenant-qualified composite FK to the planning record (nullable).
        builder.HasOne(a => a.AssetAllocation)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.AssetAllocationId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_item_resource_allocations_asset_allocations_tenant_id");

        // Tenant-qualified composite FK: the assigned resource must belong to the same tenant.
        builder.HasOne<RentalAsset>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.RentalAssetId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
