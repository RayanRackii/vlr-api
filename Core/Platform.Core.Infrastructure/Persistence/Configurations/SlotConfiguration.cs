using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class SlotConfiguration : IEntityTypeConfiguration<Slot>
{
    public void Configure(EntityTypeBuilder<Slot> builder)
    {
        builder.ToTable("slots", "rentals");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.RentalAssetId).IsRequired();
        builder.Property(s => s.Date).IsRequired();
        builder.Property(s => s.StartTime).IsRequired();
        builder.Property(s => s.EndTime).IsRequired();
        builder.Property(s => s.OccupancyKindId).IsRequired();
        builder.Property(s => s.Label).HasMaxLength(200);
        builder.Property(s => s.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(SlotStatus.Available)
            .IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.RentalAssetId, s.Date, s.StartTime })
            .IsUnique();

        builder.HasIndex(s => new { s.TenantId, s.Date, s.Status });

        builder.HasOne(s => s.RentalAsset)
            .WithMany()
            .HasForeignKey(s => s.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.OccupancyKind)
            .WithMany()
            .HasForeignKey(s => s.OccupancyKindId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Reservation)
            .WithMany()
            .HasForeignKey(s => s.ReservationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(s => s.SourceTemplate)
            .WithMany()
            .HasForeignKey(s => s.SourceTemplateId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
