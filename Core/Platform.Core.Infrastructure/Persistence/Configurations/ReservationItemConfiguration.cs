using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ReservationItemConfiguration : IEntityTypeConfiguration<ReservationItem>
{
    public void Configure(EntityTypeBuilder<ReservationItem> builder)
    {
        builder.ToTable("reservation_items", "rentals");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .ValueGeneratedNever();

        builder.Property(i => i.TenantId)
            .IsRequired();

        builder.Property(i => i.ReservationId)
            .IsRequired();

        builder.Property(i => i.RentalAssetId)
            .IsRequired();

        builder.Property(i => i.Quantity)
            .IsRequired();

        builder.Property(i => i.UnitPrice)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(i => i.SubTotal)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(i => i.CreatedAt)
            .IsRequired();

        builder.HasIndex(i => new { i.TenantId, i.ReservationId });

        // FK only to rentals.rental_assets — never to inventory/maintenance.
        builder.HasOne(i => i.RentalAsset)
            .WithMany()
            .HasForeignKey(i => i.RentalAssetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
