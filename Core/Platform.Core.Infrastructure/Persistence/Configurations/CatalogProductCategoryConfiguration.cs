using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductCategoryConfiguration : IEntityTypeConfiguration<CatalogProductCategory>
{
    public void Configure(EntityTypeBuilder<CatalogProductCategory> builder)
    {
        builder.ToTable("catalog_product_categories", "catalog");
        builder.HasKey(x => new { x.TenantId, x.ProductId, x.CategoryId });
        builder.HasIndex(x => new { x.TenantId, x.CategoryId, x.ProductId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CatalogProduct>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ProductId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TaxonomyCategory>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.CategoryId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
