using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantModuleMenuItemConfiguration
    : IEntityTypeConfiguration<TenantModuleMenuItem>
{
    public void Configure(EntityTypeBuilder<TenantModuleMenuItem> builder)
    {
        builder.ToTable("tenant_module_menu_items", "core");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .ValueGeneratedNever();

        builder.Property(m => m.TenantId)
            .IsRequired();

        builder.Property(m => m.ModuleName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.Label)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(m => m.SortOrder)
            .IsRequired();

        builder.Property(m => m.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.SortOrder });

        builder.HasIndex(m => new { m.TenantId, m.ModuleName, m.IsActive });

        // Cross-schema FK to rentals.rental_assets (optional).
        builder.HasOne(m => m.RentalAsset)
            .WithMany()
            .HasForeignKey(m => m.RentalAssetId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
