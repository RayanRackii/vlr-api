using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetWorkingHoursConfiguration : IEntityTypeConfiguration<AssetWorkingHours>
{
    public void Configure(EntityTypeBuilder<AssetWorkingHours> builder)
    {
        builder.ToTable("asset_working_hours", "planning", table =>
        {
            table.HasCheckConstraint(
                "ck_asset_working_hours_day_of_week",
                "day_of_week >= 0 AND day_of_week <= 6");
            table.HasCheckConstraint(
                "ck_asset_working_hours_close_after_open",
                "close_time > open_time");
        });

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Id)
            .ValueGeneratedNever();

        builder.Property(h => h.TenantId)
            .IsRequired();

        builder.Property(h => h.AssetId)
            .IsRequired();

        builder.Property(h => h.DayOfWeek)
            .IsRequired();

        builder.Property(h => h.OpenTime)
            .IsRequired();

        builder.Property(h => h.CloseTime)
            .IsRequired();

        builder.HasIndex(h => new { h.TenantId, h.AssetId, h.DayOfWeek, h.OpenTime })
            .IsUnique()
            .HasDatabaseName("ux_asset_working_hours_tenant_asset_day_open");

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(h => new { h.TenantId, h.AssetId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_asset_working_hours_assets_tenant_id_asset_id");
    }
}
