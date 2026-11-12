using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class RentalAssetConfiguration : IEntityTypeConfiguration<RentalAsset>
{
    public void Configure(EntityTypeBuilder<RentalAsset> builder)
    {
        builder.ToTable("rental_assets", "rentals", table =>
        {
            table.HasCheckConstraint(
                "ck_rental_assets_advance_days",
                "max_advance_booking_days IS NULL OR (max_advance_booking_days >= 0 AND max_advance_booking_days <= 3650)");
            table.HasCheckConstraint(
                "ck_rental_assets_lead_minutes",
                "minimum_booking_lead_minutes IS NULL OR (minimum_booking_lead_minutes >= 0 AND minimum_booking_lead_minutes <= 525600)");
            table.HasCheckConstraint(
                "ck_rental_assets_deposit_percent",
                "deposit_percent IS NULL OR (deposit_percent >= 0 AND deposit_percent <= 100)");
            table.HasCheckConstraint("ck_rental_assets_capacity_people", "capacity_people IS NULL OR (capacity_people > 0 AND capacity_people <= 100000)");
        });

        builder.HasKey(a => a.Id);
        builder.HasAlternateKey(a => new { a.TenantId, a.Id });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ResourceVisualTemplateRevision>().WithMany()
            .HasForeignKey(a => a.VisualTemplateRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.VisualTemplateRevisionId);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.AssetId)
            .IsRequired();

        builder.Property(a => a.Type)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.TotalQuantity)
            .IsRequired();

        builder.Property(a => a.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(a => a.RequiresDeposit)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(a => a.SchedulePolicy)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(SchedulePolicy.SlotGrid)
            .IsRequired();

        builder.Property(a => a.OpenTime);

        builder.Property(a => a.CloseTime);

        builder.Property(a => a.AllowedDurationMinutes)
            .HasMaxLength(128);

        builder.Property(a => a.QueueEnabled)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(a => a.QueueOpeningTime);

        builder.Property(a => a.MaxAdvanceBookingDays);

        builder.Property(a => a.CancelCutoffHours);

        builder.Property(a => a.MinimumBookingLeadMinutes);

        builder.Property(a => a.DepositPercent);

        // Database default covers rows that already exist. The sentinel stays the CLR
        // default so an explicit false is written on update; true matches the default.
        builder.Property(a => a.DirectRental)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => a.AssetId)
            .IsUnique();

        builder.HasIndex(a => new { a.TenantId, a.IsActive });

        builder.HasMany(a => a.Pricings)
            .WithOne(p => p.RentalAsset)
            .HasForeignKey(p => p.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(a => a.Pricings)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
