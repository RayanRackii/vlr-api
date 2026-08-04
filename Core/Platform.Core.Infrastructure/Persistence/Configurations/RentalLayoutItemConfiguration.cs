using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class RentalLayoutItemConfiguration : IEntityTypeConfiguration<RentalLayoutItem>
{
    public void Configure(EntityTypeBuilder<RentalLayoutItem> builder)
    {
        builder.ToTable("layout_items", "rentals");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.LayoutId).IsRequired();
        builder.Property(i => i.RentalAssetId).IsRequired();
        builder.Property(i => i.XPercent).IsRequired();
        builder.Property(i => i.YPercent).IsRequired();
        builder.Property(i => i.WidthPercent).IsRequired();
        builder.Property(i => i.HeightPercent).IsRequired();
        builder.Property(i => i.ZIndex).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasIndex(i => new { i.LayoutId, i.RentalAssetId }).IsUnique();

        builder.HasOne(i => i.RentalAsset)
            .WithMany()
            .HasForeignKey(i => i.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
