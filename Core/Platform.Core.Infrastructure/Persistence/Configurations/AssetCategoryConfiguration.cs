using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetCategoryConfiguration : IEntityTypeConfiguration<AssetCategory>
{
    public void Configure(EntityTypeBuilder<AssetCategory> builder)
    {
        builder.ToTable("asset_categories", "assets");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .IsRequired();

        builder.Property(c => c.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(c => c.Manufacturer)
            .HasMaxLength(200);

        builder.Property(c => c.Description)
            .HasMaxLength(1000);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.Property(c => c.ScheduledDeletionAt);

        builder.HasIndex(c => new { c.TenantId, c.Name })
            .IsUnique();

        builder.HasIndex(c => c.ScheduledDeletionAt);

        builder.HasMany(c => c.Assets)
            .WithOne(a => a.Category)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Assets)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
