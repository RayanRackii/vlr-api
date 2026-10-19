using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class WorkOrderTaskFileConfiguration : IEntityTypeConfiguration<WorkOrderTaskFile>
{
    public void Configure(EntityTypeBuilder<WorkOrderTaskFile> builder)
    {
        builder.ToTable("work_order_task_files", "os");

        builder.HasKey(file => file.Id);

        builder.Property(file => file.Id)
            .ValueGeneratedNever();

        builder.Property(file => file.TenantId)
            .IsRequired();

        builder.Property(file => file.WorkOrderId)
            .IsRequired();

        builder.Property(file => file.WorkOrderTaskId)
            .IsRequired();

        builder.Property(file => file.StorageKey)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(file => file.FileName)
            .HasMaxLength(260)
            .IsRequired();

        builder.Property(file => file.ContentType)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(file => file.SizeBytes)
            .IsRequired();

        builder.Property(file => file.CreatedAt)
            .IsRequired();

        builder.HasIndex(file => file.WorkOrderTaskId)
            .IsUnique();

        builder.HasIndex(file => new { file.TenantId, file.StorageKey })
            .IsUnique();

        builder.HasOne<WorkOrderTask>()
            .WithMany()
            .HasForeignKey(file => file.WorkOrderTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
