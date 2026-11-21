using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.ToTable("tenant_subscriptions", "billing");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .IsRequired();

        builder.Property(s => s.PlanCatalogId)
            .IsRequired();

        builder.Property(s => s.BillingCycle)
            .HasMaxLength(TenantSubscription.CodeMaxLength)
            .IsRequired();

        builder.Property(s => s.Status)
            .HasMaxLength(TenantSubscription.CodeMaxLength)
            .IsRequired();

        builder.Property(s => s.BillingMode)
            .HasMaxLength(TenantSubscription.CodeMaxLength)
            .HasDefaultValue(BillingModes.Provider)
            .IsRequired();

        builder.Property(s => s.LimitsUnlimited)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(s => s.DiscountPercent)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(s => s.Provider)
            .HasMaxLength(TenantSubscription.ProviderMaxLength);

        builder.Property(s => s.ProviderCustomerId)
            .HasMaxLength(TenantSubscription.ProviderIdMaxLength);

        builder.Property(s => s.ProviderSubscriptionId)
            .HasMaxLength(TenantSubscription.ProviderIdMaxLength);

        builder.Property(s => s.RowVersion)
            .IsConcurrencyToken()
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_tenant_subscriptions_tenants_tenant_id");

        builder.HasOne(s => s.PlanCatalog)
            .WithMany()
            .HasForeignKey(s => s.PlanCatalogId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_tenant_subscriptions_plan_catalog_plan_catalog_id");

        // One current subscription per tenant.
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasDatabaseName("ux_tenant_subscriptions_tenant_current")
            .HasFilter("ended_at IS NULL");

        builder.HasIndex(s => s.PlanCatalogId);

        // One provider subscription per tenant row: a retry can never double charge.
        builder.HasIndex(s => s.ProviderSubscriptionId)
            .IsUnique()
            .HasDatabaseName("ux_tenant_subscriptions_provider_subscription")
            .HasFilter("provider_subscription_id IS NOT NULL");
    }
}
