using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class WorkOrderTaskConfiguration : IEntityTypeConfiguration<WorkOrderTask>
{
    public void Configure(EntityTypeBuilder<WorkOrderTask> builder)
    {
        builder.ToTable("work_order_tasks", "os");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.TenantId)
            .IsRequired();

        builder.Property(t => t.WorkOrderId)
            .IsRequired();

        builder.Property(t => t.Title)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(t => t.InputType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.Configuration)
            .HasColumnType("jsonb");

        builder.Property(t => t.IsMandatory)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.Order)
            .IsRequired();

        builder.Property(t => t.Value)
            .HasMaxLength(8000);

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.WorkOrderId, t.Order });

        builder.HasOne(t => t.WorkOrder)
            .WithMany(w => w.Tasks)
            .HasForeignKey(t => t.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.PlanTask)
            .WithMany()
            .HasForeignKey(t => t.PlanTaskId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
