using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .ValueGeneratedNever();

        builder.Property(u => u.TenantId)
            .IsRequired();

        builder.Property(u => u.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(u => u.Code)
            .HasMaxLength(50);

        builder.Property(u => u.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        builder.HasIndex(u => new { u.TenantId, u.Name })
            .IsUnique();

        builder.HasIndex(u => new { u.TenantId, u.Code })
            .IsUnique()
            .HasFilter("code IS NOT NULL");
    }
}
