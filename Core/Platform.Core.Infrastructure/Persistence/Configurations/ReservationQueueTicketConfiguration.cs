using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ReservationQueueTicketConfiguration
    : IEntityTypeConfiguration<ReservationQueueTicket>
{
    public void Configure(EntityTypeBuilder<ReservationQueueTicket> builder)
    {
        builder.ToTable("reservation_queue_tickets", "rentals");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.TenantId)
            .IsRequired();

        builder.Property(t => t.QueueSessionId)
            .IsRequired();

        builder.Property(t => t.CustomerId)
            .IsRequired();

        builder.Property(t => t.Sequence)
            .IsRequired();

        builder.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.JoinedAt)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.QueueSessionId, t.Sequence })
            .IsUnique();

        builder.HasIndex(t => new { t.QueueSessionId, t.CustomerId })
            .IsUnique()
            .HasFilter("status IN ('Waiting', 'Active')");

        builder.HasOne(t => t.Customer)
            .WithMany()
            .HasForeignKey(t => t.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.CompletedReservation)
            .WithMany()
            .HasForeignKey(t => t.CompletedReservationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
