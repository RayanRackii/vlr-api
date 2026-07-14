using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class RentalPricingConfiguration : IEntityTypeConfiguration<RentalPricing>
{
    public void Configure(EntityTypeBuilder<RentalPricing> builder)
    {
        builder.ToTable("rental_pricings", "rentals");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.RentalAssetId)
            .IsRequired();

        builder.Property(p => p.DayOfWeek)
            .IsRequired();

        builder.Property(p => p.StartTime)
            .IsRequired();

        builder.Property(p => p.EndTime)
            .IsRequired();

        builder.Property(p => p.PricePerHour)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(p => p.RequiresDeposit)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(p => p.DepositPercentage)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.RentalAssetId, p.DayOfWeek });
    }
}
