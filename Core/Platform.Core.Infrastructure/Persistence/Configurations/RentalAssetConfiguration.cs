using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class RentalAssetConfiguration : IEntityTypeConfiguration<RentalAsset>
{
    public void Configure(EntityTypeBuilder<RentalAsset> builder)
    {
        builder.ToTable("rental_assets", "rentals");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.AssetId)
            .IsRequired();

        builder.Property(a => a.Type)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.TotalQuantity)
            .IsRequired();

        builder.Property(a => a.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => a.AssetId)
            .IsUnique();

        builder.HasIndex(a => new { a.TenantId, a.IsActive });

        builder.HasMany(a => a.Pricings)
            .WithOne(p => p.RentalAsset)
            .HasForeignKey(p => p.RentalAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(a => a.Pricings)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
