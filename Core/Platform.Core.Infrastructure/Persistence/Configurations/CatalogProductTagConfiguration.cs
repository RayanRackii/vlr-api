using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogProductTagConfiguration : IEntityTypeConfiguration<CatalogProductTag>
{
    public void Configure(EntityTypeBuilder<CatalogProductTag> builder)
    {
        builder.ToTable("catalog_product_tags", "catalog");
        builder.HasKey(x => new { x.TenantId, x.ProductId, x.TagId });
        builder.HasIndex(x => new { x.TenantId, x.TagId, x.ProductId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CatalogProduct>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ProductId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TaxonomyTag>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.TagId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
