using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ReservationQueueSessionConfiguration
    : IEntityTypeConfiguration<ReservationQueueSession>
{
    public void Configure(EntityTypeBuilder<ReservationQueueSession> builder)
    {
        builder.ToTable("reservation_queue_sessions", "rentals");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .IsRequired();

        builder.Property(s => s.RentalAssetId)
            .IsRequired();

        builder.Property(s => s.OpeningDate)
            .IsRequired();

        builder.Property(s => s.OpensAt)
            .IsRequired();

        builder.Property(s => s.WaitingRoomOpensAt)
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.RentalAssetId, s.OpeningDate })
            .IsUnique();

        builder.HasOne(s => s.RentalAsset)
            .WithMany()
            .HasForeignKey(s => s.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Tickets)
            .WithOne(t => t.QueueSession)
            .HasForeignKey(t => t.QueueSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Tickets)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
