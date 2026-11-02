using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations", "rentals", table =>
        {
            // CustomerBooking requires customer identity; InternalUse has none. `purpose` is
            // stored as its string enum name (HasConversion<string>() below), so the literal
            // must be the string 'InternalUse', not the underlying int value.
            table.HasCheckConstraint(
                "ck_reservations_customer_booking_requires_customer",
                "purpose = 'InternalUse' OR (customer_id IS NOT NULL AND customer_name IS NOT NULL AND customer_whats_app IS NOT NULL)");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedNever();

        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.UnitId)
            .IsRequired();

        builder.Property(r => r.Purpose)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(ReservationPurpose.CustomerBooking)
            .IsRequired();

        builder.Property(r => r.CustomerId);

        builder.Property(r => r.CustomerName)
            .HasMaxLength(200);

        builder.Property(r => r.CustomerWhatsApp)
            .HasMaxLength(32);

        builder.Property(r => r.StartDateTime)
            .IsRequired();

        builder.Property(r => r.EndDateTime)
            .IsRequired();

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(ReservationStatus.PendingDeposit)
            .IsRequired();

        builder.Property(r => r.TotalAmount)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(r => r.DepositPaid)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.Status });

        builder.HasIndex(r => new { r.TenantId, r.StartDateTime, r.EndDateTime });

        builder.HasIndex(r => new { r.TenantId, r.CustomerId });

        // Internal reservation scan: used to find active internal-use occupancy windows.
        builder.HasIndex(r => new { r.TenantId, r.Purpose, r.Status, r.EndDateTime });

        // Tenant-qualified composite FK: application checks alone (ResourceOccupancyService) must
        // not be the only thing standing between a caller-supplied unitId and a cross-tenant unit.
        builder.HasOne(r => r.Unit)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.UnitId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Cross-schema FK to core.customers is allowed (isolation rule is rentals ↛ inventory/maintenance).
        builder.HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Items)
            .WithOne(i => i.Reservation)
            .HasForeignKey(i => i.ReservationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
