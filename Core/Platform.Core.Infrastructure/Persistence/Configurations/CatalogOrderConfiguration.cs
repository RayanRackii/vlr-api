using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogOrderConfiguration : IEntityTypeConfiguration<CatalogOrder>
{
    public void Configure(EntityTypeBuilder<CatalogOrder> builder)
    {
        builder.ToTable("catalog_orders", "catalog");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .ValueGeneratedNever();

        builder.Property(o => o.TenantId)
            .IsRequired();

        builder.Property(o => o.CustomerId)
            .IsRequired();

        builder.Property(o => o.OrderNumber)
            .IsRequired();

        builder.Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CatalogOrderStatus.Requested)
            .IsRequired();

        builder.Property(o => o.CustomerNote)
            .HasMaxLength(2000);

        builder.Property(o => o.CustomerNameSnapshot)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(o => o.CustomerEmailSnapshot)
            .HasMaxLength(256);

        builder.Property(o => o.CustomerPhoneSnapshot)
            .HasMaxLength(32);

        builder.Property(o => o.TotalAmount)
            .HasColumnType("decimal(18,2)");

        builder.Property(o => o.Currency)
            .HasMaxLength(3)
            .HasDefaultValue("BRL")
            .IsRequired();

        builder.Property(o => o.RejectedReason)
            .HasMaxLength(1000);

        builder.Property(o => o.CancelledReason)
            .HasMaxLength(1000);

        builder.Property(o => o.RowVersion)
            .IsConcurrencyToken()
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(o => o.CreatedAt)
            .IsRequired();

        builder.HasIndex(o => new { o.TenantId, o.OrderNumber })
            .IsUnique();

        builder.HasIndex(o => new { o.TenantId, o.Status });

        builder.HasIndex(o => new { o.TenantId, o.CustomerId });

        builder.HasIndex(o => new { o.TenantId, o.CreatedAt });

        builder.HasOne(o => o.Customer)
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Items)
            .WithOne(i => i.Order)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.History)
            .WithOne(h => h.Order)
            .HasForeignKey(h => h.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Navigation(o => o.History)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
