using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionEventConfiguration : IEntityTypeConfiguration<SubscriptionEvent>
{
    public void Configure(EntityTypeBuilder<SubscriptionEvent> builder)
    {
        builder.ToTable("subscription_events", "billing");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.TenantId)
            .IsRequired();

        builder.Property(e => e.SubscriptionId)
            .IsRequired();

        builder.Property(e => e.Type)
            .HasMaxLength(SubscriptionEvent.TypeMaxLength)
            .IsRequired();

        builder.Property(e => e.ProviderEventId)
            .HasMaxLength(SubscriptionEvent.ProviderEventIdMaxLength);

        builder.Property(e => e.PayloadJson)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        // Append-only: the table has no updated_at.
        builder.Ignore(e => e.UpdatedAt);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TenantSubscription>().WithMany().HasForeignKey(e => e.SubscriptionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ProviderEventId)
            .IsUnique()
            .HasDatabaseName("ux_subscription_events_provider_event_id")
            .HasFilter("provider_event_id IS NOT NULL");

        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });

        builder.HasIndex(e => e.SubscriptionId);
    }
}
