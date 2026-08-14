using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class OccupancyKindConfiguration : IEntityTypeConfiguration<OccupancyKind>
{
    public void Configure(EntityTypeBuilder<OccupancyKind> builder)
    {
        builder.ToTable("occupancy_kinds", "rentals");

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.TenantId).IsRequired();
        builder.Property(k => k.Key).HasMaxLength(64).IsRequired();
        builder.Property(k => k.Label).HasMaxLength(120).IsRequired();
        builder.Property(k => k.Description).HasMaxLength(500);
        builder.Property(k => k.ColorHex).HasMaxLength(16);
        builder.Property(k => k.IconKey).HasMaxLength(64);
        builder.Property(k => k.IsBookableByCustomer).IsRequired();
        builder.Property(k => k.BlocksCapacity).IsRequired();
        builder.Property(k => k.SortOrder).IsRequired();
        builder.Property(k => k.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(k => k.CreatedAt).IsRequired();

        builder.HasIndex(k => new { k.TenantId, k.Key }).IsUnique();
        builder.HasIndex(k => new { k.TenantId, k.IsActive, k.SortOrder });
    }
}
