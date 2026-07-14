using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class PlanTaskConfiguration : IEntityTypeConfiguration<PlanTask>
{
    public void Configure(EntityTypeBuilder<PlanTask> builder)
    {
        builder.ToTable("plan_tasks", "pmoc");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.TenantId)
            .IsRequired();

        builder.Property(t => t.MaintenancePlanId)
            .IsRequired();

        builder.Property(t => t.Title)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(t => t.InputType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(t => t.IsMandatory)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.Order)
            .IsRequired();

        builder.Property(t => t.Configuration)
            .HasColumnType("jsonb");

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.MaintenancePlanId, t.Order });

        builder.HasOne(t => t.MaintenancePlan)
            .WithMany(p => p.Tasks)
            .HasForeignKey(t => t.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
