using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductAccessConfiguration : IEntityTypeConfiguration<CatalogProductAccess>
{
    public void Configure(EntityTypeBuilder<CatalogProductAccess> builder)
    {
        builder.ToTable("catalog_product_access", "catalog", table =>
        {
            // Exactly one of CustomerId / GroupId must be set.
            table.HasCheckConstraint(
                "ck_catalog_product_access_exactly_one_target",
                "(customer_id IS NOT NULL AND group_id IS NULL) " +
                "OR (customer_id IS NULL AND group_id IS NOT NULL)");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedNever();

        builder.Property(a => a.TenantId)
            .IsRequired();

        builder.Property(a => a.ProductId)
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.ProductId, a.CustomerId })
            .IsUnique()
            .HasFilter("customer_id IS NOT NULL");

        builder.HasIndex(a => new { a.TenantId, a.ProductId, a.GroupId })
            .IsUnique()
            .HasFilter("group_id IS NOT NULL");

        builder.HasOne<CatalogProduct>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CustomerGroup>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Customer>().WithMany()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
