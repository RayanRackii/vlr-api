using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class TaxonomyTagConfiguration : IEntityTypeConfiguration<TaxonomyTag>
{
    public void Configure(EntityTypeBuilder<TaxonomyTag> builder)
    {
        builder.ToTable("taxonomy_tags", "core");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasAlternateKey(x => new { x.TenantId, x.Id });
        builder.Property(x => x.Name).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(80).IsRequired();
        builder.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.NormalizedName).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
    }
}
