using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets", "assets");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.UnitId)
            .IsRequired();

        builder.Property(a => a.CategoryId)
            .IsRequired();

        builder.Property(a => a.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.Tag)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.Location)
            .HasMaxLength(300);

        builder.Property(a => a.SerialNumber)
            .HasMaxLength(150);

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(AssetStatus.Active)
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.Property(a => a.ScheduledDeletionAt);

        builder.HasIndex(a => new { a.TenantId, a.Tag })
            .IsUnique();

        builder.HasIndex(a => new { a.TenantId, a.UnitId });

        builder.HasIndex(a => new { a.TenantId, a.CategoryId });

        builder.HasIndex(a => a.ScheduledDeletionAt);

        builder.HasOne(a => a.Unit)
            .WithMany()
            .HasForeignKey(a => a.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Category)
            .WithMany(c => c.Assets)
            .HasForeignKey(a => a.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
