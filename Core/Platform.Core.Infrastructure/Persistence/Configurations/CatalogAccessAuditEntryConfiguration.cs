using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogAccessAuditEntryConfiguration : IEntityTypeConfiguration<CatalogAccessAuditEntry>
{
    public void Configure(EntityTypeBuilder<CatalogAccessAuditEntry> builder)
    {
        builder.ToTable("catalog_access_audit", "catalog");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(32).IsRequired();
        builder.Property(a => a.ActorName).HasMaxLength(200);
        builder.Property(a => a.BeforeJson).HasColumnType("jsonb");
        builder.Property(a => a.AfterJson).HasColumnType("jsonb");
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId, a.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
