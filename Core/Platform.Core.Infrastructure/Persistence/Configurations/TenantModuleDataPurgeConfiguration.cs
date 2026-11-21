using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantModuleDataPurgeConfiguration : IEntityTypeConfiguration<TenantModuleDataPurge>
{
    public void Configure(EntityTypeBuilder<TenantModuleDataPurge> builder)
    {
        builder.ToTable("tenant_module_data_purges", "core");

        builder.HasKey(p => p.Id).HasName("pk_tenant_module_data_purges");

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.Module)
            .HasMaxLength(TenantModuleDataPurge.ModuleMaxLength)
            .IsRequired();

        builder.Property(p => p.RequestedByUserId)
            .HasMaxLength(TenantModuleDataPurge.ActorMaxLength);

        builder.Property(p => p.RequestedAt)
            .IsRequired();

        builder.Property(p => p.ScheduledFor)
            .IsRequired();

        builder.Property(p => p.CancelledByUserId)
            .HasMaxLength(TenantModuleDataPurge.ActorMaxLength);

        builder.Property(p => p.CancelReason)
            .HasMaxLength(TenantModuleDataPurge.CancelReasonMaxLength);

        builder.Property(p => p.ResultJson)
            .HasColumnType("jsonb");

        builder.Property(p => p.RowVersion)
            .IsConcurrencyToken()
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.Ignore(p => p.IsPending);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_tenant_module_data_purges_tenants_tenant_id");

        // One pending purge per tenant and module.
        builder.HasIndex(p => new { p.TenantId, p.Module })
            .IsUnique()
            .HasDatabaseName("ux_tenant_module_data_purges_pending")
            .HasFilter("cancelled_at IS NULL AND executed_at IS NULL");
    }
}
