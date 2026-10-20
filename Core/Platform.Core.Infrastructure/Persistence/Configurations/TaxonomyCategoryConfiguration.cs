using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TaxonomyCategoryConfiguration : IEntityTypeConfiguration<TaxonomyCategory>
{
    public void Configure(EntityTypeBuilder<TaxonomyCategory> builder)
    {
        builder.ToTable("taxonomy_categories", "core");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasAlternateKey(x => new { x.TenantId, x.Id });
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(80).IsRequired();
        builder.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Icon).HasMaxLength(20);
        builder.HasOne<TaxonomyCategory>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ParentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
