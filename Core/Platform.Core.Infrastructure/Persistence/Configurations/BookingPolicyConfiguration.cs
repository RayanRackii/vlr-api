using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class BookingPolicyConfiguration : IEntityTypeConfiguration<BookingPolicy>
{
    public void Configure(EntityTypeBuilder<BookingPolicy> builder)
    {
        builder.ToTable("booking_policies", "rentals", table =>
        {
            table.HasCheckConstraint(
                "ck_booking_policies_daily_limit",
                "max_active_reservations_per_customer_per_day IS NULL OR (max_active_reservations_per_customer_per_day >= 1 AND max_active_reservations_per_customer_per_day <= 100)");
            table.HasCheckConstraint(
                "ck_booking_policies_advance_days",
                "max_advance_booking_days IS NULL OR (max_advance_booking_days >= 0 AND max_advance_booking_days <= 3650)");
        });

        builder.HasKey(policy => policy.Id);

        builder.Property(policy => policy.Id)
            .ValueGeneratedNever();

        builder.Property(policy => policy.TenantId)
            .IsRequired();

        builder.Property(policy => policy.ResourceType)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(policy => policy.CancellationPolicyText)
            .HasMaxLength(BookingPolicy.CancellationPolicyMaxLength);

        builder.Property(policy => policy.MaxActiveReservationsPerCustomerPerDay);

        builder.Property(policy => policy.MaxAdvanceBookingDays);

        builder.Property(policy => policy.CreatedAt)
            .IsRequired();

        builder.HasIndex(policy => policy.TenantId)
            .IsUnique()
            .HasFilter("resource_type IS NULL")
            .HasDatabaseName("ux_booking_policies_tenant_default");

        builder.HasIndex(policy => new { policy.TenantId, policy.ResourceType })
            .IsUnique()
            .HasFilter("resource_type IS NOT NULL")
            .HasDatabaseName("ux_booking_policies_tenant_resource_type");
    }
}
