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

        builder.HasIndex(l => new { l.TenantId, l.IsActive });

        builder.HasOne(l => l.Unit)
            .WithMany()
            .HasForeignKey(l => l.UnitId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(l => l.Items)
            .WithOne(i => i.Layout)
            .HasForeignKey(i => i.LayoutId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(l => l.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
