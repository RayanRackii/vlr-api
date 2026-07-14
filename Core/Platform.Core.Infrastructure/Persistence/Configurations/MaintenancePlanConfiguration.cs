using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        builder.ToTable("maintenance_plans", "pmoc");

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

        builder.Property(p => p.Frequency)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(p => p.AssetCategoryId)
            .IsRequired();

        builder.Property(p => p.IsActive)
            .HasDefaultValue(true)
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

        builder.HasMany(p => p.Tasks)
            .WithOne(t => t.MaintenancePlan)
            .HasForeignKey(t => t.MaintenancePlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Tasks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
