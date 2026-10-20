using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Configurations;

public sealed class CatalogPolicyConfiguration : IEntityTypeConfiguration<CatalogPolicy>
{
    public void Configure(EntityTypeBuilder<CatalogPolicy> builder)
    {
        builder.ToTable("catalog_policies", "catalog", table =>
        {
            table.HasCheckConstraint("ck_catalog_policies_order_status",
                "order_cancelable_up_to_status IN ('Requested', 'Approved', 'Preparing', 'Ready')");
            table.HasCheckConstraint("ck_catalog_policies_request_status",
                "request_cancelable_up_to_status = 'Submitted'");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.OrderCancelableUpToStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.RequestCancelableUpToStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.CancellationPolicyText).HasMaxLength(CatalogPolicy.CancellationPolicyMaxLength);
        builder.HasIndex(p => p.TenantId).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
