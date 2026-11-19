using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetPlanningProfileConfiguration : IEntityTypeConfiguration<AssetPlanningProfile>
{
    public void Configure(EntityTypeBuilder<AssetPlanningProfile> builder)
    {
        builder.ToTable("asset_planning_profiles", "planning", table =>
        {
            table.HasCheckConstraint("ck_asset_planning_profiles_capacity", "capacity >= 1");
            table.HasCheckConstraint(
                "ck_asset_planning_profiles_default_prep_minutes",
                "default_prep_minutes >= 0");
            table.HasCheckConstraint(
                "ck_asset_planning_profiles_default_cleanup_minutes",
                "default_cleanup_minutes >= 0");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.Property(p => p.AssetId)
            .IsRequired();

        // No database defaults for IsPlannable / HoursPolicy: a CLR default equal to the EF sentinel
        // would make an explicit false / Anytime silently fall back to the database default.
        builder.Property(p => p.IsPlannable)
            .IsRequired();

        builder.Property(p => p.Capacity)
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(p => p.HoursPolicy)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(p => p.DefaultPrepMinutes)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(p => p.DefaultCleanupMinutes)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.AssetId })
            .IsUnique()
            .HasDatabaseName("ux_asset_planning_profiles_tenant_asset");

        // Tenant-qualified composite FK: a profile can never point at another tenant's asset.
        // Cascade: the profile is pure configuration of the asset and must not block its hard delete.
        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.AssetId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_asset_planning_profiles_assets_tenant_id_asset_id");
    }
}
