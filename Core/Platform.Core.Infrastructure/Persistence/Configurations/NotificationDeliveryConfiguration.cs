using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries", "core");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedNever();

        builder.Property(d => d.TenantId)
            .IsRequired();

        builder.Property(d => d.NotificationId)
            .IsRequired();

        builder.Property(d => d.Channel)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.RecipientKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.RecipientName)
            .HasMaxLength(200);

        builder.Property(d => d.RecipientEmail)
            .HasMaxLength(256);

        builder.Property(d => d.RecipientPhone)
            .HasMaxLength(32);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(NotificationDeliveryStatus.Queued)
            .IsRequired();

        builder.Property(d => d.ProviderMessageId)
            .HasMaxLength(200);

        builder.Property(d => d.ErrorMessage)
            .HasMaxLength(1000);

        builder.Property(d => d.AttemptCount)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(d => d.CreatedAt)
            .IsRequired();

        builder.HasIndex(d => new { d.TenantId, d.Status, d.NextAttemptAt });

        builder.HasIndex(d => new { d.TenantId, d.Channel });

        builder.HasMany(d => d.Attempts)
            .WithOne(a => a.Delivery)
            .HasForeignKey(a => a.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(d => d.Attempts)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
