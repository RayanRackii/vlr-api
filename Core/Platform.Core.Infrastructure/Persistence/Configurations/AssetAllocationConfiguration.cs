using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetAllocationConfiguration : IEntityTypeConfiguration<AssetAllocation>
{
    public void Configure(EntityTypeBuilder<AssetAllocation> builder)
    {
        builder.ToTable("asset_allocations", "planning", table =>
        {
            table.HasCheckConstraint("ck_asset_allocations_quantity", "quantity >= 1");
            table.HasCheckConstraint(
                "ck_asset_allocations_work_window",
                "planned_work_end > planned_work_start");
            table.HasCheckConstraint(
                "ck_asset_allocations_occupied_covers_work",
                "occupied_start <= planned_work_start AND occupied_end >= planned_work_end");
            table.HasCheckConstraint(
                "ck_asset_allocations_single_link",
                "catalog_order_item_id IS NULL OR work_order_id IS NULL");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.AssetId)
            .IsRequired();

        builder.Property(a => a.UnitId)
            .IsRequired();

        builder.Property(a => a.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.Title)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.Notes)
            .HasMaxLength(1000);

        builder.Property(a => a.Quantity)
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(a => a.PlannedWorkStart)
            .IsRequired();

        builder.Property(a => a.PlannedWorkEnd)
            .IsRequired();

        builder.Property(a => a.OccupiedStart)
            .IsRequired();

        builder.Property(a => a.OccupiedEnd)
            .IsRequired();

        builder.Property(a => a.TimeZoneId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(AllocationStatus.Scheduled)
            .IsRequired();

        builder.Property(a => a.Source)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.ReferenceLabel)
            .HasMaxLength(120);

        builder.Property(a => a.CancelReason)
            .HasMaxLength(1000);

        builder.Property(a => a.RowVersion)
            .IsConcurrencyToken()
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.AssetId, a.OccupiedStart })
            .HasDatabaseName("ix_asset_allocations_tenant_asset_occupied_start");

        // Blocking statuses only: the overlap query never needs Released / Cancelled rows.
        builder.HasIndex(a => new { a.TenantId, a.AssetId })
            .HasDatabaseName("ix_asset_allocations_tenant_asset_blocking")
            .HasFilter("status IN ('Scheduled', 'Running', 'Cleaning')");

        builder.HasIndex(a => new { a.TenantId, a.WorkOrderId })
            .HasDatabaseName("ix_asset_allocations_tenant_work_order");

        builder.HasIndex(a => new { a.TenantId, a.CatalogOrderItemId })
            .HasDatabaseName("ix_asset_allocations_tenant_catalog_order_item");

        builder.HasIndex(a => new { a.TenantId, a.ReservationItemId })
            .IsUnique()
            .HasDatabaseName("ux_asset_allocations_tenant_reservation_item")
            .HasFilter("reservation_item_id IS NOT NULL");

        // Tenant-qualified composite FKs. Restrict everywhere: allocations are history and must
        // never disappear because a linked row is deleted.
        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.AssetId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_allocations_assets_tenant_id_asset_id");

        builder.HasOne<CatalogOrderItem>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CatalogOrderItemId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_allocations_catalog_order_items_tenant_id_item_id");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.WorkOrderId })
            .HasPrincipalKey(w => new { w.TenantId, w.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_allocations_work_orders_tenant_id_work_order_id");

        builder.HasOne<ReservationItem>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ReservationItemId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_asset_allocations_reservation_items_tenant_id_item_id");
    }
}
