using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetFamilyConfiguration : IEntityTypeConfiguration<AssetFamily>
{
    public void Configure(EntityTypeBuilder<AssetFamily> builder)
    {
        builder.ToTable("asset_families", "assets");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedNever();

        builder.Property(f => f.Key)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(f => f.Label)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(f => f.FieldSchemaJson)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(f => f.SortOrder)
            .IsRequired();

        builder.Property(f => f.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.HasIndex(f => f.Key)
            .IsUnique();
    }
}
