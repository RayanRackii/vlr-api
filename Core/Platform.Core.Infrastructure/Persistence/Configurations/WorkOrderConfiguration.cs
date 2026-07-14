using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("work_orders", "os");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id)
            .ValueGeneratedNever();

        builder.Property(w => w.TenantId)
            .IsRequired();

        builder.Property(w => w.AssetId)
            .IsRequired();

        builder.Property(w => w.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(w => w.ScheduledDate)
            .IsRequired();

        builder.Property(w => w.Notes)
            .HasMaxLength(4000);

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        builder.HasIndex(w => new { w.TenantId, w.Status });

        builder.HasIndex(w => new { w.TenantId, w.AssetId, w.ScheduledDate });

        builder.HasIndex(w => new { w.TenantId, w.MaintenancePlanId, w.AssetId, w.ScheduledDate });

        builder.HasOne(w => w.Asset)
            .WithMany()
            .HasForeignKey(w => w.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.MaintenancePlan)
            .WithMany()
            .HasForeignKey(w => w.MaintenancePlanId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(w => w.Tasks)
            .WithOne(t => t.WorkOrder)
            .HasForeignKey(t => t.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(w => w.Tasks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
