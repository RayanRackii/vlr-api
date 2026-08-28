using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantNotificationChannelConfigConfiguration
    : IEntityTypeConfiguration<TenantNotificationChannelConfig>
{
    public void Configure(EntityTypeBuilder<TenantNotificationChannelConfig> builder)
    {
        builder.ToTable("tenant_notification_channel_configs", "core");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .IsRequired();

        builder.Property(c => c.EventType)
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(c => c.Channel)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(c => c.IsActive)
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.EventType, c.Channel })
            .IsUnique();
    }
}
