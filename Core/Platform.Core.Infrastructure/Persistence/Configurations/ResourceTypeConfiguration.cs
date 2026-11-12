using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class ResourceTypeConfiguration : IEntityTypeConfiguration<ResourceType>
{
    public void Configure(EntityTypeBuilder<ResourceType> builder)
    {
        builder.ToTable("resource_types", "rentals", table =>
        {
            table.HasCheckConstraint("ck_resource_types_article", "article IN ('um', 'uma')");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.Key).HasMaxLength(40).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(80).IsRequired();
        builder.Property(t => t.Singular).HasMaxLength(80).IsRequired();
        builder.Property(t => t.Plural).HasMaxLength(80).IsRequired();
        builder.Property(t => t.Article).HasMaxLength(3).IsRequired();
        builder.Property(t => t.Seeded).HasDefaultValue(false).IsRequired();
        builder.Property(t => t.Bookable).HasDefaultValue(false).IsRequired();
        builder.Property(t => t.Deliverable).HasDefaultValue(false).IsRequired();
        builder.Property(t => t.Maintenance).HasDefaultValue(false).IsRequired();
        builder.Property(t => t.FieldsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
