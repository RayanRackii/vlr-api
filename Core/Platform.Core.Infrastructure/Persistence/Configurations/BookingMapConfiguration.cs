using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class BookingMapSettingsConfiguration : IEntityTypeConfiguration<BookingMapSettings>
{
    public void Configure(EntityTypeBuilder<BookingMapSettings> b)
    {
        b.ToTable("booking_map_settings", "rentals");
        b.HasKey(x => x.TenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.AvailableColor).HasMaxLength(7).IsRequired();
        b.Property(x => x.TakenColor).HasMaxLength(7).IsRequired();
        b.Property(x => x.UnavailableColor).HasMaxLength(7).IsRequired();
        b.Property(x => x.SelectedColor).HasMaxLength(7).IsRequired();
        b.Property(x => x.TimeFilterMode).HasMaxLength(16).IsRequired();
        b.Property(x => x.DefaultView).HasMaxLength(8).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<RentalLayout>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.DefaultLayoutId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class LayoutDecorationConfiguration : IEntityTypeConfiguration<LayoutDecoration>
{
    public void Configure(EntityTypeBuilder<LayoutDecoration> b)
    {
        b.ToTable("layout_decorations", "rentals");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.Label).HasMaxLength(120);
        b.Property(x => x.IconKey).HasMaxLength(80);
        b.Property(x => x.StyleJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.GeometryJson).HasColumnType("jsonb");
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RentalLayout>().WithMany().HasForeignKey(x => new { x.TenantId, x.LayoutId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.LayoutId, x.ZIndex });
    }
}
