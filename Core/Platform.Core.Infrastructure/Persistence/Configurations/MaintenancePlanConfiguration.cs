using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        builder.ToTable("maintenance_plans", "pmoc", table =>
            table.HasCheckConstraint(
                "ck_maintenance_plans_interval_days",
                $"interval_days >= {PmocScheduling.MinIntervalDays} AND interval_days <= {PmocScheduling.MaxIntervalDays}"));

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.UnitId)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(2000);

        builder.Property(p => p.IntervalDays)
            .IsRequired();

        builder.Property(p => p.FirstDueDate)
            .IsRequired();

        builder.Property(p => p.AssetCategoryId)
            .IsRequired();

        builder.Property(p => p.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(p => p.OriginKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(p => p.SourceTemplateId);

        builder.Property(p => p.SourceTemplateVersion);

        builder.Property(p => p.AutoGenerateEnabled)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.Name });

        builder.HasIndex(p => new { p.TenantId, p.UnitId });

        builder.HasIndex(p => new { p.TenantId, p.AssetCategoryId });

        builder.HasOne(p => p.Unit)
            .WithMany()
            .HasForeignKey(p => p.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.AssetCategory)
            .WithMany()
            .HasForeignKey(p => p.AssetCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GlobalMaintenanceTemplate>()
            .WithMany()
            .HasForeignKey(p => p.SourceTemplateId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(p => p.Tasks)
            .WithOne(t => t.MaintenancePlan)
            .HasForeignKey(t => t.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Tasks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
