using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class RentalLayoutConfiguration : IEntityTypeConfiguration<RentalLayout>
{
    public void Configure(EntityTypeBuilder<RentalLayout> builder)
    {
        builder.ToTable("layouts", "rentals");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.Property(l => l.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(l => l.AspectRatio).HasDefaultValue(1.6d).IsRequired();
        builder.Property(l => l.WidthPercent).HasDefaultValue(100d).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();

        builder.Property(l => l.SortOrder).HasDefaultValue(0);
        builder.Property(l => l.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasAlternateKey(l => new { l.TenantId, l.Id });
        builder.HasIndex(l => new { l.TenantId, l.IsActive, l.SortOrder });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Unit)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.UnitId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(l => l.Items)
            .WithOne(i => i.Layout)
            .HasForeignKey(i => new { i.TenantId, i.LayoutId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(l => l.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
