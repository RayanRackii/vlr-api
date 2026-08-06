using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantAssetFamilyConfiguration : IEntityTypeConfiguration<TenantAssetFamily>
{
    public void Configure(EntityTypeBuilder<TenantAssetFamily> builder)
    {
        builder.ToTable("tenant_asset_families", "assets");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.TenantId)
            .IsRequired();

        builder.Property(t => t.FamilyId)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.FamilyId })
            .IsUnique();

        builder.HasOne(t => t.Tenant)
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Family)
            .WithMany()
            .HasForeignKey(t => t.FamilyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
