using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class NotificationDeliveryAttemptConfiguration
    : IEntityTypeConfiguration<NotificationDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryAttempt> builder)
    {
        builder.ToTable("notification_delivery_attempts", "core");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.DeliveryId)
            .IsRequired();

        builder.Property(a => a.AttemptNumber)
            .IsRequired();

        builder.Property(a => a.StartedAt)
            .IsRequired();

        builder.Property(a => a.Outcome)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.ProviderResponse)
            .HasMaxLength(2000);

        builder.Property(a => a.ErrorMessage)
            .HasMaxLength(1000);

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => new { a.DeliveryId, a.AttemptNumber })
            .IsUnique();
    }
}
