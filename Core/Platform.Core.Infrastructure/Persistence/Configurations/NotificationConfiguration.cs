using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", "core");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id)
            .ValueGeneratedNever();

        builder.Property(n => n.TenantId)
            .IsRequired();

        builder.Property(n => n.EventType)
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(n => n.AggregateType)
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(n => n.AggregateId)
            .IsRequired();

        builder.Property(n => n.Payload)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(n => n.CreatedAt)
            .IsRequired();

        builder.HasIndex(n => new { n.TenantId, n.CreatedAt });

        builder.HasIndex(n => new { n.TenantId, n.EventType });

        builder.HasIndex(n => new { n.TenantId, n.AggregateType, n.AggregateId });

        builder.HasMany(n => n.Deliveries)
            .WithOne(d => d.Notification)
            .HasForeignKey(d => d.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(n => n.Deliveries)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
