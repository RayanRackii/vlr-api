using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.LegalName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.TradeName)
            .HasMaxLength(200);

        builder.Property(t => t.TaxId)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(t => t.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.HasIndex(t => t.TaxId)
            .IsUnique();

        builder.HasMany(t => t.Users)
            .WithOne(u => u.Tenant)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Units)
            .WithOne(u => u.Tenant)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Roles)
            .WithOne(r => r.Tenant)
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(t => t.Users)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Navigation(t => t.Units)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Navigation(t => t.Roles)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
